using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace ServerTaming;

// Server-only taming and breeding for animals that have no Tameable component in vanilla (Deer, Neck).
// Players need no mod: everything runs on ZDO data near connected players.
//  - Taming: a hungry wild animal eats matching food lying within FeedRadius; while fed (and calm),
//    its taming time counts down. At zero the owner client is asked to set the vanilla "tamed" flag
//    (Character.RPC_SetTamed), which vanilla clients already understand (no fleeing/attacking players).
//  - Breeding: fed tamed animals with a tamed partner nearby gain love points, get pregnant and
//    give birth to a tamed adult of the same prefab (no custom offspring prefab).
//  - Food can also come from chests, with the same rules as ServersideQoL TameAssist's
//    FeedFromContainers (global range, per-chest range on a sign, leave-at-least). When TameAssist is
//    installed its settings and taming/fed multipliers are used, so deer and necks behave like its tames.
// Progress lives in server memory. Writing it into client-owned ZDOs would be lost to the owner's
// updates, so it is only copied into the ZDOs (without a revision bump) just before the world saves.
[BepInPlugin(Guid, "ServerTaming", Version)]
public sealed class ServerTamingPlugin : BaseUnityPlugin
{
	public const string Guid = "Tie.ServerTaming";

	public const string Version = "1.1.6";

	private static readonly int KeyTameLeft = "ServerTaming_tameLeft".GetStableHashCode();

	private static readonly int KeyFedUntil = "ServerTaming_fedUntil".GetStableHashCode();

	private static readonly int KeyLove = "ServerTaming_love".GetStableHashCode();

	private static readonly int KeyPregnant = "ServerTaming_pregnant".GetStableHashCode();

	private const string TameAssistGuid = "ArgusMagnus.ServersideQoL.TameAssist";

	private static ServerTamingPlugin _instance;

	private ConfigEntry<float> _tickSeconds;

	private ConfigEntry<float> _feedRadius;

	private ConfigEntry<bool> _requireCalm;

	private ConfigEntry<float> _messageRange;

	private ConfigEntry<bool> _progressMessages;

	private ConfigEntry<int> _maxTameStars;

	private ConfigEntry<bool> _useTameAssist;

	private ConfigEntry<bool> _feedWildFromContainers;

	private ConfigEntry<float> _containerRange;

	private ConfigEntry<int> _leaveAtLeast;

	private ConfigEntry<float> _breedCheckSeconds;

	private ConfigEntry<float> _loveChance;

	private ConfigEntry<int> _requiredLovePoints;

	private ConfigEntry<float> _partnerRange;

	private ConfigEntry<float> _populationRange;

	private ConfigEntry<int> _maxCreatures;

	private ConfigEntry<int> _maxTamedNearby;

	private ConfigEntry<bool> _debugLog;

	private readonly List<Species> _species = new List<Species>();

	private readonly Dictionary<int, Species> _speciesByPrefab = new Dictionary<int, Species>();

	private readonly HashSet<int> _foodPrefabs = new HashSet<int>();

	private readonly Dictionary<ZDOID, State> _states = new Dictionary<ZDOID, State>();

	// States of animals the server just destroyed, waiting for the copy that replaces them.
	private readonly List<Orphan> _orphans = new List<Orphan>();

	private readonly List<ZDO> _sectorObjects = new List<ZDO>();

	private readonly HashSet<ZDOID> _seen = new HashSet<ZDOID>();

	private readonly List<ZDO> _animals = new List<ZDO>();

	private readonly List<ZDO> _foods = new List<ZDO>();

	private readonly HashSet<ZDOID> _eaten = new HashSet<ZDOID>();

	private readonly List<ZDO> _chests = new List<ZDO>();

	private readonly List<ZDO> _signs = new List<ZDO>();

	private readonly Dictionary<ZDOID, float> _chestRanges = new Dictionary<ZDOID, float>();

	private readonly Dictionary<int, PrefabKind> _kinds = new Dictionary<int, PrefabKind>();

	private Feeding _feeding = new Feeding();

	private enum PrefabKind
	{
		Other,
		Chest,
		Sign
	}

	// Container feeding and time multipliers, from TameAssist when it is installed.
	private sealed class Feeding
	{
		public bool FromTameAssist;

		public bool Containers;

		public float Range;

		public Regex SignRange;

		public int MaxRange = 64;

		public int LeaveAtLeast;

		public float TamingMultiplier = 1f;

		public float FedMultiplier = 1f;

		public string ProgressMessageType = "";
	}

	private bool _resolved;

	private float _timer;

	private double _lastTick = -1.0;

	private int _ticks;

	private sealed class Species
	{
		public string Prefab;

		public ConfigEntry<bool> Enabled;

		public ConfigEntry<string> Foods;

		public ConfigEntry<float> TamingMinutes;

		public ConfigEntry<float> FedMinutes;

		public ConfigEntry<bool> Breeding;

		public ConfigEntry<float> PregnancyMinutes;

		public ConfigEntry<int> MaxCreatures;

		public int Hash;

		public string Name;

		public HashSet<int> FoodHashes = new HashSet<int>();

		public ZNetView View;
	}

	private sealed class Orphan
	{
		public int Prefab;

		public Vector3 Pos;

		public int Level;

		public float At;

		public ZDOID From;

		public State State;
	}

	private sealed class State
	{
		public float TameLeft;

		public double FedUntil;

		public int Love;

		public double Pregnant;

		public double NextBreedCheck;

		public bool TameAnnounced;
	}

	private void Awake()
	{
		_instance = this;
		// Before 1.1.4 the plugin ID, and so the config file name, was local.servertaming.
		string oldConfig = Path.Combine(Paths.ConfigPath, "local.servertaming.cfg");
		if (!File.Exists(Config.ConfigFilePath) && File.Exists(oldConfig))
		{
			File.Move(oldConfig, Config.ConfigFilePath);
			Config.Reload();
			Logger.LogInfo("Moved local.servertaming.cfg to " + Path.GetFileName(Config.ConfigFilePath) + ".");
		}
		_tickSeconds = Config.Bind("General", "TickSeconds", 5f, "How often (seconds) animals near players are checked.");
		_feedRadius = Config.Bind("General", "FeedRadius", 8f, "Animals eat matching food lying within this many metres. They do not walk to it, so keep them penned with the food inside.");
		_requireCalm = Config.Bind("General", "RequireCalm", true, "Taming and breeding pause while the animal is alerted (fleeing or fighting), like vanilla.");
		_messageRange = Config.Bind("General", "MessageRange", 30f, "Players within this many metres see taming and birth messages.");
		_progressMessages = Config.Bind("General", "ProgressMessages", true, "Show taming progress to nearby players: at 25/50/75%, or with TameAssist's TamingProgressMessageType when TameAssist is used.");
		_debugLog = Config.Bind("General", "DebugLog", false, "Log feeding, love and breeding decisions for every animal near players (verbose; for troubleshooting).");
		_maxTameStars = Config.Bind("General", "MaxTameStars", -1, "Highest star count that can be tamed; -1 means any (including the 3+ star creatures from level mods such as ServersideQoL CreatureLevelUp). Offspring keep their parent's stars.");
		_useTameAssist = Config.Bind("Containers", "UseTameAssistSettings", true, "When ServersideQoL TameAssist is installed, use its FeedFromContainers settings (range, chest sign range, LeaveAtLeast), its TamingTimeMultiplier and FedDurationMultiplier, and its TamingProgressMessageType. The two settings below are then ignored.");
		_containerRange = Config.Bind("Containers", "FeedFromContainersRange", 0f, "Without TameAssist: animals eat from chests within this many metres (0 = off).");
		_leaveAtLeast = Config.Bind("Containers", "LeaveAtLeast", 1, "Without TameAssist: keep at least this many of each food in a chest.");
		_feedWildFromContainers = Config.Bind("Containers", "FeedWildFromContainers", true, "Wild animals being tamed also eat from chests (TameAssist itself only feeds tamed animals from chests, but deer and necks can't walk to food).");
		_breedCheckSeconds = Config.Bind("Breeding", "CheckSeconds", 30f, "How often each fed tamed animal tries to gain a love point.");
		_loveChance = Config.Bind("Breeding", "LoveChance", 0.5f, "Chance (0-1) of gaining a love point on each check when a partner is near.");
		_requiredLovePoints = Config.Bind("Breeding", "RequiredLovePoints", 4, "Love points needed to become pregnant.");
		_partnerRange = Config.Bind("Breeding", "PartnerRange", 5f, "A tamed partner of the same kind must be within this many metres.");
		_populationRange = Config.Bind("Breeding", "PopulationRange", 15f, "Radius used to count animals of the same kind for MaxCreatures.");
		_maxCreatures = Config.Bind("Breeding", "MaxCreatures", 8, "No new love points when this many animals of the same kind are within PopulationRange. Each animal's own MaxCreatures setting overrides this.");
		_maxTamedNearby = Config.Bind("Breeding", "MaxTamedNearby", 30, "No breeding when this many tamed animals of the same kind are anywhere in the area loaded around players, wherever they wandered (0 = no limit). A safety net for animals that leave the pen, where MaxCreatures only sees their small group.");
		AddSpecies("Deer", "Raspberry,Blueberries,Cloudberry,Carrot,Turnip,Onion,Mushroom,MushroomYellow", 25f, 5f, 10f);
		AddSpecies("Neck", "FishRaw", 25f, 5f, 10f);
		new Terminal.ConsoleCommand("tamestatus", "[radius] - show server taming/breeding progress of animals near you; use through 'server tamestatus'", Status);
		Harmony harmony = new Harmony(Guid);
		harmony.PatchAll(typeof(SavePatch));
		harmony.PatchAll(typeof(DestroyPatch));
	}

	private void AddSpecies(string prefab, string foods, float taming, float fed, float pregnancy)
	{
		_species.Add(new Species
		{
			Prefab = prefab,
			Enabled = Config.Bind(prefab, "Enabled", true, "Make " + prefab + " tameable and breedable."),
			Foods = Config.Bind(prefab, "Foods", foods, "Comma-separated item prefab names it eats."),
			TamingMinutes = Config.Bind(prefab, "TamingMinutes", taming, "Minutes of being fed (and calm) needed to tame it."),
			FedMinutes = Config.Bind(prefab, "FedMinutes", fed, "Minutes one food item keeps it fed."),
			Breeding = Config.Bind(prefab, "Breeding", true, "Tamed ones breed and give birth to tamed adults."),
			PregnancyMinutes = Config.Bind(prefab, "PregnancyMinutes", pregnancy, "Minutes from pregnancy to birth."),
			MaxCreatures = Config.Bind(prefab, "MaxCreatures", -1, "No new love points when this many " + prefab + " are within [Breeding] PopulationRange; -1 uses [Breeding] MaxCreatures.")
		});
	}

	// Prefab names can only be checked once ZNetScene has loaded them.
	private void Resolve()
	{
		_resolved = true;
		foreach (Species s in _species)
		{
			GameObject go = ZNetScene.instance.GetPrefab(s.Prefab);
			if (!s.Enabled.Value || go == null || go.GetComponent<Character>() == null)
			{
				if (s.Enabled.Value)
				{
					Logger.LogWarning("Creature prefab '" + s.Prefab + "' not found; it is disabled.");
				}
				continue;
			}
			s.Hash = s.Prefab.GetStableHashCode();
			s.Name = go.GetComponent<Character>().m_name;
			s.View = go.GetComponent<ZNetView>();
			foreach (string food in s.Foods.Value.Split(',').Select(f => f.Trim()).Where(f => f.Length > 0))
			{
				GameObject item = ZNetScene.instance.GetPrefab(food);
				if (item == null || item.GetComponent<ItemDrop>() == null)
				{
					Logger.LogWarning("Food '" + food + "' for " + s.Prefab + " is not an item prefab; ignored.");
					continue;
				}
				s.FoodHashes.Add(food.GetStableHashCode());
				_foodPrefabs.Add(food.GetStableHashCode());
			}
			_speciesByPrefab[s.Hash] = s;
			Logger.LogInfo($"{s.Prefab}: {s.FoodHashes.Count} food(s), taming {s.TamingMinutes.Value} min, breeding {(s.Breeding.Value ? "on" : "off")}.");
		}
	}

	private void Update()
	{
		if (ZNet.instance == null || !ZNet.instance.IsServer() || ZDOMan.instance == null || ZNetScene.instance == null)
		{
			_resolved = false;
			_states.Clear();
			_orphans.Clear();
			_lastTick = -1.0;
			return;
		}
		_timer += Time.deltaTime;
		if (_timer < Mathf.Max(1f, _tickSeconds.Value))
		{
			return;
		}
		_timer = 0f;
		try
		{
			if (!_resolved)
			{
				Resolve();
			}
			Tick();
		}
		catch (Exception ex)
		{
			Logger.LogError(ex);
		}
	}

	private void Tick()
	{
		double now = ZNet.instance.GetTimeSeconds();
		float dt = _lastTick < 0.0 ? 0f : Mathf.Clamp((float)(now - _lastTick), 0f, Mathf.Max(1f, _tickSeconds.Value) * 3f);
		_lastTick = now;
		if (_speciesByPrefab.Count == 0)
		{
			return;
		}
		_feeding = ReadFeeding();
		Collect();
		UpdateChestRanges();
		_eaten.Clear();
		foreach (ZDO zdo in _animals)
		{
			Species s = _speciesByPrefab[zdo.GetPrefab()];
			State st = GetState(zdo);
			bool tamed = zdo.GetBool(ZDOVars.s_tamed);
			if (!tamed && !CanTame(zdo))
			{
				continue;
			}
			bool calm = !_requireCalm.Value || !zdo.GetBool(ZDOVars.s_alert);
			if (now >= st.FedUntil)
			{
				TryEat(zdo, s, st, now, tamed);
			}
			bool fed = now < st.FedUntil;
			if (!tamed)
			{
				UpdateTaming(zdo, s, st, fed, calm, dt);
			}
			else if (s.Breeding.Value)
			{
				UpdateBreeding(zdo, s, st, fed && calm, now);
			}
		}
		if (++_ticks % 60 == 0)
		{
			foreach (ZDOID id in _states.Keys.Where(id => ZDOMan.instance.GetZDO(id) == null).ToList())
			{
				_states.Remove(id);
			}
		}
	}

	// Animals and food in the areas simulated around connected players (vanilla only runs these too).
	private void Collect()
	{
		_animals.Clear();
		_foods.Clear();
		_chests.Clear();
		_signs.Clear();
		_seen.Clear();
		SimulationDistance synced = ZNet.instance.GetSyncedSimulationDistance();
		SimulationDistance near = new SimulationDistance(synced.NearSimulationDistance, 0, synced.IsClassic);
		List<Vector3> centers = ZNet.instance.GetPeers().Select(p => p.GetRefPos()).ToList();
		if (!ZNet.instance.IsDedicated())
		{
			centers.Add(ZNet.instance.GetReferencePosition());
		}
		foreach (Vector3 center in centers)
		{
			_sectorObjects.Clear();
			ZDOMan.instance.FindSectorObjects(ZoneSystem.GetZone(center), near, _sectorObjects);
			foreach (ZDO zdo in _sectorObjects)
			{
				if (!zdo.IsValid() || !_seen.Add(zdo.m_uid))
				{
					continue;
				}
				int prefab = zdo.GetPrefab();
				if (_speciesByPrefab.ContainsKey(prefab))
				{
					_animals.Add(zdo);
				}
				else if (_foodPrefabs.Contains(prefab) && !zdo.GetBool(ZDOVars.s_piece))
				{
					_foods.Add(zdo);
				}
				else if (_feeding.Containers)
				{
					switch (Classify(prefab))
					{
					case PrefabKind.Chest:
						_chests.Add(zdo);
						break;
					case PrefabKind.Sign:
						_signs.Add(zdo);
						break;
					}
				}
			}
		}
	}

	private State GetState(ZDO zdo)
	{
		if (!_states.TryGetValue(zdo.m_uid, out State st) && (st = Adopt(zdo)) == null)
		{
			// Values saved into the ZDO at the last world save; ZDOIDs change on every load.
			st = new State
			{
				TameLeft = zdo.GetFloat(KeyTameLeft, -1f),
				FedUntil = zdo.GetLong(KeyFedUntil, 0L) / 1000.0,
				Love = zdo.GetInt(KeyLove),
				Pregnant = zdo.GetLong(KeyPregnant, 0L) / 1000.0
			};
			_states[zdo.m_uid] = st;
		}
		return st;
	}

	// Other server mods replace a creature's ZDO with a copy under a new ZDOID: ServersideQoL
	// CreatureLevelUp does it for 3+ star creatures to show their stars and size. The copy carries the
	// values from the last world save only, so the live state moves over to it here.
	private void Orphaned(ZDO zdo)
	{
		if (!_states.TryGetValue(zdo.m_uid, out State st))
		{
			return;
		}
		_states.Remove(zdo.m_uid);
		_orphans.RemoveAll(o => Time.time - o.At > 60f);
		_orphans.Add(new Orphan
		{
			Prefab = zdo.GetPrefab(),
			Pos = zdo.GetPosition(),
			Level = zdo.GetInt(ZDOVars.s_level, 1),
			At = Time.time,
			From = zdo.m_uid,
			State = st
		});
	}

	private State Adopt(ZDO zdo)
	{
		if (_orphans.Count == 0)
		{
			return null;
		}
		int prefab = zdo.GetPrefab();
		int level = zdo.GetInt(ZDOVars.s_level, 1);
		Vector3 pos = zdo.GetPosition();
		Orphan best = _orphans.Where(o => o.Prefab == prefab && o.Level == level && Time.time - o.At <= 60f && (o.Pos - pos).sqrMagnitude <= 100f)
			.OrderBy(o => (o.Pos - pos).sqrMagnitude).FirstOrDefault();
		if (best == null)
		{
			return null;
		}
		_orphans.Remove(best);
		_states[zdo.m_uid] = best.State;
		Debug($"{zdo.m_uid}: took over the state of replaced {best.From} (level {level}).");
		return best.State;
	}

	private string Describe(ZDO zdo)
	{
		Vector3 p = zdo.GetPosition();
		return $"{_speciesByPrefab[zdo.GetPrefab()].Prefab} {zdo.m_uid} ({zdo.GetInt(ZDOVars.s_level, 1) - 1} star(s), {(zdo.GetBool(ZDOVars.s_tamed) ? "tamed" : "wild")}, owner {zdo.GetOwner()}) at ({p.x:0}, {p.y:0}, {p.z:0})";
	}

	private void Debug(string text)
	{
		if (_debugLog.Value)
		{
			Logger.LogInfo(text);
		}
	}

	[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.DestroyZDO))]
	private static class DestroyPatch
	{
		private static void Prefix(ZDO zdo)
		{
			try
			{
				if (zdo != null && _instance != null && _instance._speciesByPrefab.ContainsKey(zdo.GetPrefab()))
				{
					_instance.Orphaned(zdo);
				}
			}
			catch (Exception ex)
			{
				_instance?.Logger.LogError(ex);
			}
		}
	}

	private PrefabKind Classify(int prefab)
	{
		if (!_kinds.TryGetValue(prefab, out PrefabKind kind))
		{
			GameObject go = ZNetScene.instance.GetPrefab(prefab);
			kind = go == null ? PrefabKind.Other
				: go.GetComponent<Container>() != null && go.GetComponent<Piece>() != null ? PrefabKind.Chest
				: go.GetComponent<Sign>() != null ? PrefabKind.Sign
				: PrefabKind.Other;
			_kinds[prefab] = kind;
		}
		return kind;
	}

	// Mirrors TameAssist's config; read every tick so changes to its config file apply live.
	private Feeding ReadFeeding()
	{
		Feeding f = new Feeding();
		// With ServersideQoL's UnifiedConfig the entries live in the core plugin's file under "<core>.TameAssist".
		List<ConfigFile> ta = !_useTameAssist.Value || !Chainloader.PluginInfos.ContainsKey(TameAssistGuid) ? null
			: Chainloader.PluginInfos.Values.Where(i => i.Metadata.GUID.StartsWith("ArgusMagnus.ServersideQoL", StringComparison.Ordinal) && i.Instance != null)
				.Select(i => i.Instance.Config).ToList();
		if (ta != null && Get(ta, "Enabled", true))
		{
			f.FromTameAssist = true;
			f.Containers = Get(ta, "FeedFromContainers", false);
			f.Range = Get(ta, "FeedFromContainersRange", 0f);
			f.MaxRange = Get(ta, "FeedFromContainersMaxRange", 64);
			f.LeaveAtLeast = Get(ta, "FeedFromContainersLeaveAtLeast", 1);
			f.TamingMultiplier = Get(ta, "TamingTimeMultiplier", 1f);
			f.FedMultiplier = Get(ta, "FedDurationMultiplier", 1f);
			f.ProgressMessageType = Get(ta, "TamingProgressMessageType", (object)"").ToString();
			// Same pattern as ServersideQoL ContainerSigns: the prefix (with or without emoji variation selector) then a number.
			string prefix = Get(ta, "FeedFromContainersRangeSignPrefix", "");
			string bare = prefix.Replace("\ufe0f", "");
			if (prefix.Length > 0)
			{
				f.SignRange = new Regex((prefix == bare ? Regex.Escape(prefix) : "(?:" + Regex.Escape(prefix) + "|" + Regex.Escape(bare) + ")") + "(?<R>\\d+)");
			}
		}
		else
		{
			f.Range = _containerRange.Value;
			f.Containers = f.Range > 0f;
			f.LeaveAtLeast = _leaveAtLeast.Value;
		}
		return f;
	}

	private static T Get<T>(List<ConfigFile> configs, string key, T fallback)
	{
		foreach (ConfigFile config in configs)
		{
			foreach (ConfigDefinition def in config.Keys)
			{
				if (def.Key == key && (def.Section == "TameAssist" || def.Section.EndsWith(".TameAssist", StringComparison.Ordinal)) && config[def].BoxedValue is T value)
				{
					return value;
				}
			}
		}
		return fallback;
	}

	// A chest's feed range is the global range, or the number after the prefix in its text, capped at
	// MaxRange. ServersideQoL ContainerSigns keeps that text on the chest itself and copies it onto signs
	// it recreates at will, so the chest is checked first and nearby signs only as a fallback.
	private void UpdateChestRanges()
	{
		_chestRanges.Clear();
		if (!_feeding.Containers)
		{
			return;
		}
		foreach (ZDO chest in _chests)
		{
			if (SignRange(chest) is int range)
			{
				_chestRanges[chest.m_uid] = Mathf.Min(range, _feeding.MaxRange);
			}
			else if (_feeding.Range > 0f)
			{
				_chestRanges[chest.m_uid] = _feeding.Range;
			}
		}
		foreach (ZDO sign in _signs)
		{
			if (!(SignRange(sign) is int range))
			{
				continue;
			}
			Vector3 pos = sign.GetPosition();
			ZDO chest = _chests.Where(c => (c.GetPosition() - pos).sqrMagnitude <= 9f)
				.OrderBy(c => (c.GetPosition() - pos).sqrMagnitude).FirstOrDefault();
			if (chest != null && SignRange(chest) == null)
			{
				_chestRanges[chest.m_uid] = Mathf.Min(range, _feeding.MaxRange);
			}
		}
	}

	private int? SignRange(ZDO zdo)
	{
		if (_feeding.SignRange == null)
		{
			return null;
		}
		Match match = _feeding.SignRange.Match(zdo.GetString(ZDOVars.s_text));
		return match.Success && int.TryParse(match.Groups["R"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int range) ? range : null;
	}

	private float TamingSeconds(Species s)
	{
		return Mathf.Max(1f, s.TamingMinutes.Value * 60f * _feeding.TamingMultiplier);
	}

	private void TryEat(ZDO animal, Species s, State st, double now, bool tamed)
	{
		string source = TryEatFromGround(animal, s) ? "the ground" : (tamed || _feedWildFromContainers.Value) && TryEatFromChest(animal, s) ? "a chest" : null;
		if (source != null)
		{
			st.FedUntil = now + s.FedMinutes.Value * 60.0 * _feeding.FedMultiplier;
		}
		if (_debugLog.Value)
		{
			Debug($"{Describe(animal)} is hungry: " + (source != null ? "ate from " + source + "." : $"no food found ({_chests.Count(c => _chestRanges.ContainsKey(c.m_uid))} feeding chest(s) loaded)."));
		}
	}

	private bool TryEatFromChest(ZDO animal, Species s)
	{
		if (!_feeding.Containers)
		{
			return false;
		}
		Vector3 pos = animal.GetPosition();
		foreach (ZDO chest in _chests.OrderBy(c => (c.GetPosition() - pos).sqrMagnitude))
		{
			if (_chestRanges.TryGetValue(chest.m_uid, out float range) && (chest.GetPosition() - pos).sqrMagnitude <= range * range && TakeOneFood(chest, s.FoodHashes, _feeding.LeaveAtLeast))
			{
				return true;
			}
		}
		return false;
	}

	// Takes one food item out of a chest by editing its saved inventory bytes in place: a stack is
	// decremented or the item entry removed. Working on the bytes keeps every other item exactly as it
	// was, even items the server's ObjectDB doesn't know.
	private static bool TakeOneFood(ZDO chest, HashSet<int> foods, int leaveAtLeast)
	{
		if (chest.GetBool(ZDOVars.s_inUse))
		{
			return false;
		}
		// Chests store their inventory as a raw ZPackage byte array (Container.Save).
		byte[] data = chest.GetByteArray(ZDOVars.s_items);
		if (data == null || data.Length == 0)
		{
			return false;
		}
		byte[] bytes;
		int count;
		List<(int Start, int End, int Hash, int Stack)> items = new List<(int, int, int, int)>();
		try
		{
			ZPackage pkg = new ZPackage(data);
			int version = pkg.ReadInt();
			if (version < (int)global::Version.Item.Smaller)
			{
				return false;
			}
			count = pkg.ReadUShort();
			for (int i = 0; i < count; i++)
			{
				int start = pkg.GetPos();
				ItemDrop.ItemData item = new ItemDrop.ItemData();
				int hash = ItemDrop.ItemData.Load(pkg, item, (global::Version.Item)version);
				items.Add((start, pkg.GetPos(), hash, item.m_stack));
			}
			bytes = pkg.GetArray();
		}
		catch (Exception)
		{
			return false;
		}
		foreach (int food in foods)
		{
			List<(int Start, int End, int Hash, int Stack)> stacks = items.Where(x => x.Hash == food && x.Stack > 0).OrderBy(x => x.Stack).ToList();
			if (stacks.Sum(x => x.Stack) - 1 < Math.Max(0, leaveAtLeast))
			{
				continue;
			}
			var target = stacks[0];
			// Item layout: int durability, byte x, byte y, byte worldLevel, byte flags, [ushort quality], [ushort stack], ...
			byte flags = bytes[target.Start + 7];
			byte[] result;
			if (target.Stack > 1 && (flags & 8) != 0)
			{
				result = (byte[])bytes.Clone();
				int offset = target.Start + 8 + ((flags & 4) != 0 ? 2 : 0);
				ushort stack = (ushort)(target.Stack - 1);
				result[offset] = (byte)(stack & 0xFF);
				result[offset + 1] = (byte)(stack >> 8);
			}
			else
			{
				result = new byte[bytes.Length - (target.End - target.Start)];
				Buffer.BlockCopy(bytes, 0, result, 0, target.Start);
				Buffer.BlockCopy(bytes, target.End, result, target.Start, bytes.Length - target.End);
				ushort remaining = (ushort)(count - 1);
				result[4] = (byte)(remaining & 0xFF);
				result[5] = (byte)(remaining >> 8);
			}
			if (!chest.IsOwner())
			{
				chest.SetOwner(ZDOMan.GetSessionID());
			}
			chest.Set(ZDOVars.s_items, result);
			return true;
		}
		return false;
	}

	private bool TryEatFromGround(ZDO animal, Species s)
	{
		Vector3 pos = animal.GetPosition();
		float best = _feedRadius.Value * _feedRadius.Value;
		ZDO food = null;
		foreach (ZDO item in _foods)
		{
			if (!s.FoodHashes.Contains(item.GetPrefab()) || _eaten.Contains(item.m_uid))
			{
				continue;
			}
			Vector3 d = item.GetPosition() - pos;
			if (Mathf.Abs(d.y) > 4f)
			{
				continue;
			}
			float d2 = d.x * d.x + d.z * d.z;
			if (d2 <= best)
			{
				best = d2;
				food = item;
			}
		}
		if (food == null)
		{
			return false;
		}
		// Own the item before changing it so the client that simulated it cannot overwrite the change.
		if (!food.IsOwner())
		{
			food.SetOwner(ZDOMan.GetSessionID());
		}
		int stack = food.GetInt(ZDOVars.s_stack, 1);
		if (stack > 1)
		{
			food.Set(ZDOVars.s_stack, stack - 1);
		}
		else
		{
			_eaten.Add(food.m_uid);
			ZDOMan.instance.DestroyZDO(food);
		}
		return true;
	}

	private void UpdateTaming(ZDO zdo, Species s, State st, bool fed, bool calm, float dt)
	{
		float total = TamingSeconds(s);
		if (st.TameLeft < 0f || st.TameLeft > total)
		{
			st.TameLeft = total;
		}
		if (fed && calm && st.TameLeft > 0f)
		{
			int before = Step(st.TameLeft, total);
			st.TameLeft = Mathf.Max(0f, st.TameLeft - dt);
			int after = Step(st.TameLeft, total);
			if (_progressMessages.Value && !_feeding.FromTameAssist && after > before && after < 4)
			{
				Message(zdo.GetPosition(), MessageHud.MessageType.TopLeft, $"{s.Name}: taming {after * 25}%");
			}
		}
		// Like TameAssist: once taming has started, show progress with its message type on every update.
		if (_progressMessages.Value && _feeding.FromTameAssist && st.TameLeft > 0f && st.TameLeft < total)
		{
			string text = string.Format(fed ? "$hud_tameness {0:P0}" : "$hud_tameness {0:P0}, $hud_tamehungry", 1f - st.TameLeft / total);
			TameAssistMessage(zdo.GetPosition(), text);
		}
		if (st.TameLeft <= 0f)
		{
			Tame(zdo, s, st);
		}
	}

	// Level 1 is no star, level 3 is two stars.
	private bool CanTame(ZDO zdo)
	{
		return _maxTameStars.Value < 0 || zdo.GetInt(ZDOVars.s_level, 1) - 1 <= _maxTameStars.Value;
	}

	private static int Step(float left, float total)
	{
		return (int)((1f - left / total) * 4f);
	}

	// Repeats every tick until the flag is set, in case ownership moved while the RPC was in flight.
	private void Tame(ZDO zdo, Species s, State st)
	{
		long owner = zdo.GetOwner();
		if (owner == 0L || owner == ZDOMan.GetSessionID())
		{
			zdo.Set(ZDOVars.s_tamed, value: true);
		}
		else
		{
			ZRoutedRpc.instance.InvokeRoutedRPC(owner, zdo.m_uid, "RPC_SetTamed", true);
		}
		if (!st.TameAnnounced)
		{
			st.TameAnnounced = true;
			Message(zdo.GetPosition(), MessageHud.MessageType.Center, s.Name + " $hud_tamedone");
			Logger.LogInfo($"Tamed {s.Prefab} at {Format(zdo.GetPosition())}.");
		}
	}

	private void UpdateBreeding(ZDO zdo, Species s, State st, bool ready, double now)
	{
		if (st.Pregnant > 0.0)
		{
			if (now - st.Pregnant >= s.PregnancyMinutes.Value * 60.0)
			{
				st.Pregnant = 0.0;
				// Checked again: the herd may have grown (or wandered in) during the pregnancy.
				string crowded = Crowded(zdo, s, atBirth: true);
				if (crowded != null)
				{
					Logger.LogInfo($"{s.Prefab} birth at {Format(zdo.GetPosition())} skipped: {crowded}.");
					return;
				}
				Birth(zdo, s);
			}
			return;
		}
		if (!ready || now < st.NextBreedCheck)
		{
			return;
		}
		float interval = Mathf.Max(1f, _breedCheckSeconds.Value);
		st.NextBreedCheck = now + interval * UnityEngine.Random.Range(1f, 1.5f);
		if (UnityEngine.Random.value > _loveChance.Value)
		{
			return;
		}
		string full = Crowded(zdo, s, atBirth: false);
		if (full != null)
		{
			Debug($"{Describe(zdo)}: no love point ({full}).");
			return;
		}
		Debug($"{Describe(zdo)}: love {st.Love + 1}/{Mathf.Max(1, _requiredLovePoints.Value)}.");
		if (++st.Love >= Mathf.Max(1, _requiredLovePoints.Value))
		{
			st.Love = 0;
			st.Pregnant = now;
		}
	}

	// Why this animal can't breed now, or null. Before a love point, pregnant animals nearby count once
	// more each for the birth they will give, so many pregnancies at once can't overshoot MaxCreatures.
	// At birth only the animals themselves count, and no partner is needed.
	private string Crowded(ZDO zdo, Species s, bool atBirth)
	{
		Vector3 pos = zdo.GetPosition();
		float population2 = _populationRange.Value * _populationRange.Value;
		float partner2 = _partnerRange.Value * _partnerRange.Value;
		int count = 0;
		int tamed = 0;
		bool partner = false;
		foreach (ZDO other in _animals)
		{
			if (other.GetPrefab() != s.Hash)
			{
				continue;
			}
			bool otherTamed = other.GetBool(ZDOVars.s_tamed);
			if (otherTamed)
			{
				tamed++;
			}
			float d2 = (other.GetPosition() - pos).sqrMagnitude;
			if (d2 <= population2)
			{
				count++;
				if (!atBirth && other != zdo && _states.TryGetValue(other.m_uid, out State os) && os.Pregnant > 0.0)
				{
					count++;
				}
			}
			if (other != zdo && d2 <= partner2 && otherTamed)
			{
				partner = true;
			}
		}
		int max = s.MaxCreatures.Value >= 0 ? s.MaxCreatures.Value : _maxCreatures.Value;
		if (!atBirth && !partner)
		{
			return "no tamed partner in range";
		}
		if (count >= max)
		{
			return $"{count} of MaxCreatures {max} nearby{(atBirth ? "" : ", counting pregnancies")}";
		}
		if (_maxTamedNearby.Value > 0 && tamed >= _maxTamedNearby.Value)
		{
			return $"{tamed} tamed {s.Prefab} around players, MaxTamedNearby {_maxTamedNearby.Value}";
		}
		return null;
	}

	// Creates the offspring directly as a ZDO (like ZNetView.Awake does) so the server does not need
	// the area loaded; the nearest client takes ownership and instantiates a vanilla tamed adult.
	// The server has no colliders loaded, so it can't see fences. Both parents are inside the pen, so
	// the offspring goes somewhere on the line between the mother and her nearest tamed partner, which
	// stays inside any convex pen; without a partner nearby it appears on the mother.
	private void Birth(ZDO parent, Species s)
	{
		Vector3 mother = parent.GetPosition();
		ZDO partner = _animals.Where(a => a != parent && a.GetPrefab() == s.Hash && a.GetBool(ZDOVars.s_tamed) && (a.GetPosition() - mother).sqrMagnitude <= 100f)
			.OrderBy(a => (a.GetPosition() - mother).sqrMagnitude).FirstOrDefault();
		Vector3 pos = partner == null ? mother : Vector3.Lerp(mother, partner.GetPosition(), UnityEngine.Random.Range(0.3f, 0.7f));
		pos.y += 0.5f;
		ZDO zdo = ZDOMan.instance.CreateNewZDO(pos, s.Hash);
		zdo.Persistent = s.View.m_persistent;
		zdo.Type = s.View.m_type;
		zdo.Distant = s.View.m_distant;
		zdo.SetPrefab(s.Hash);
		zdo.SetRotation(Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
		zdo.Set(ZDOVars.s_tamed, value: true);
		int level = parent.GetInt(ZDOVars.s_level, 1);
		if (level > 1)
		{
			zdo.Set(ZDOVars.s_level, level);
		}
		zdo.SetOwner(0L);
		Message(pos, MessageHud.MessageType.Center, "A new " + s.Name + " was born");
		Logger.LogInfo($"{s.Prefab} born at {Format(pos)}.");
	}

	// TameAssist's MessageTypes: None, TopLeftNear, TopLeftFar, CenterNear, CenterFar, InWorld.
	private void TameAssistMessage(Vector3 pos, string text)
	{
		string type = _feeding.ProgressMessageType;
		if (type == "InWorld")
		{
			float range2 = _messageRange.Value * _messageRange.Value;
			foreach (ZNetPeer peer in ZNet.instance.GetPeers())
			{
				if ((peer.GetRefPos() - pos).sqrMagnitude <= range2)
				{
					// Same payload as DamageText.ShowText: type, position, text, from-player flag.
					ZPackage pkg = new ZPackage();
					pkg.Write((int)DamageText.TextType.Normal);
					pkg.Write(pos);
					pkg.Write(text);
					pkg.Write(false);
					ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "RPC_DamageText", pkg);
				}
			}
		}
		else if (type.StartsWith("TopLeft", StringComparison.Ordinal))
		{
			Message(pos, MessageHud.MessageType.TopLeft, text);
		}
		else if (type.StartsWith("Center", StringComparison.Ordinal))
		{
			Message(pos, MessageHud.MessageType.Center, text);
		}
	}

	private void Message(Vector3 pos, MessageHud.MessageType type, string text)
	{
		float range2 = _messageRange.Value * _messageRange.Value;
		foreach (ZNetPeer peer in ZNet.instance.GetPeers())
		{
			if ((peer.GetRefPos() - pos).sqrMagnitude <= range2)
			{
				ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "ShowMessage", (int)type, text);
			}
		}
	}

	private void WriteToZdos()
	{
		foreach (KeyValuePair<ZDOID, State> pair in _states)
		{
			if (ZDOMan.instance.GetZDO(pair.Key) == null)
			{
				continue;
			}
			// No revision bump: this only needs to reach the save, not the clients.
			ZDOExtraData.Set(pair.Key, KeyTameLeft, pair.Value.TameLeft);
			ZDOExtraData.Set(pair.Key, KeyFedUntil, (long)(pair.Value.FedUntil * 1000.0));
			ZDOExtraData.Set(pair.Key, KeyLove, pair.Value.Love);
			ZDOExtraData.Set(pair.Key, KeyPregnant, (long)(pair.Value.Pregnant * 1000.0));
		}
	}

	[HarmonyPatch(typeof(ZDOMan), nameof(ZDOMan.PrepareSave))]
	private static class SavePatch
	{
		private static void Prefix()
		{
			try
			{
				_instance?.WriteToZdos();
			}
			catch (Exception ex)
			{
				_instance?.Logger.LogError(ex);
			}
		}
	}

	private void Status(Terminal.ConsoleEventArgs args)
	{
		Terminal output = args.Context;
		if (ZNet.instance == null || !ZNet.instance.IsServer())
		{
			Print(output, "tamestatus must run on the server: type 'server tamestatus' instead.");
			return;
		}
		float radius = 30f;
		if (args.Args.Length > 1 && float.TryParse(args.Args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float r) && r > 0f)
		{
			radius = r;
		}
		Vector3? center = CallerPosition();
		if (!center.HasValue)
		{
			Print(output, "Could not find your position.");
			return;
		}
		if (!_resolved)
		{
			Resolve();
		}
		_feeding = ReadFeeding();
		Collect();
		UpdateChestRanges();
		double now = ZNet.instance.GetTimeSeconds();
		List<ZDO> near = _animals.Where(z => (z.GetPosition() - center.Value).sqrMagnitude <= radius * radius)
			.OrderBy(z => (z.GetPosition() - center.Value).sqrMagnitude).ToList();
		Print(output, $"{near.Count} tameable animal(s) within {radius:0} m.");
		foreach (ZDO zdo in near.Take(20))
		{
			Species s = _speciesByPrefab[zdo.GetPrefab()];
			State st = GetState(zdo);
			float dist = Vector3.Distance(zdo.GetPosition(), center.Value);
			string fed = now < st.FedUntil ? $"fed {(st.FedUntil - now) / 60.0:0.0} min" : "hungry";
			string text;
			int stars = zdo.GetInt(ZDOVars.s_level, 1) - 1;
			if (!zdo.GetBool(ZDOVars.s_tamed) && !CanTame(zdo))
			{
				text = $"wild, {stars} star(s): above MaxTameStars, cannot be tamed";
			}
			else if (!zdo.GetBool(ZDOVars.s_tamed))
			{
				float total = TamingSeconds(s);
				float left = st.TameLeft < 0f ? total : st.TameLeft;
				text = $"wild, taming {(1f - left / total) * 100f:0}% ({left / 60f:0.0} min left), {fed}";
			}
			else if (st.Pregnant > 0.0)
			{
				text = $"tamed, pregnant ({(s.PregnancyMinutes.Value * 60.0 - (now - st.Pregnant)) / 60.0:0.0} min left), {fed}";
			}
			else
			{
				text = $"tamed, love {st.Love}/{_requiredLovePoints.Value}, {fed}";
			}
			Print(output, $"{s.Prefab} {new string('*', Math.Max(0, stars))} {dist:0} m: {text}");
		}
		Print(output, $"Chest feeding: {(_feeding.Containers ? "on" : "off")}{(_feeding.FromTameAssist ? " (TameAssist settings)" : "")}, global range {_feeding.Range:0} m, leave at least {_feeding.LeaveAtLeast}, wild animals {(_feedWildFromContainers.Value ? "yes" : "no")}.");
		HashSet<int> allFoods = new HashSet<int>(_species.SelectMany(x => x.FoodHashes));
		foreach (ZDO chest in _chests.Where(c => (c.GetPosition() - center.Value).sqrMagnitude <= (radius + 64f) * (radius + 64f))
			.OrderBy(c => (c.GetPosition() - center.Value).sqrMagnitude).Take(10))
		{
			string range = _chestRanges.TryGetValue(chest.m_uid, out float chestRange) ? $"range {chestRange:0} m" : "no range (no sign number, global range 0)";
			Print(output, $"Chest {Vector3.Distance(chest.GetPosition(), center.Value):0} m: {range}, {(chest.GetBool(ZDOVars.s_inUse) ? "OPEN (skipped), " : "")}food: {DescribeFood(chest, allFoods)}");
		}
	}

	private static string DescribeFood(ZDO chest, HashSet<int> foods)
	{
		byte[] data = chest.GetByteArray(ZDOVars.s_items);
		if (data == null || data.Length == 0)
		{
			return "empty";
		}
		try
		{
			ZPackage pkg = new ZPackage(data);
			int version = pkg.ReadInt();
			if (version < (int)global::Version.Item.Smaller)
			{
				return $"old inventory format {version}, skipped";
			}
			int count = pkg.ReadUShort();
			Dictionary<int, int> totals = new Dictionary<int, int>();
			for (int i = 0; i < count; i++)
			{
				ItemDrop.ItemData item = new ItemDrop.ItemData();
				int hash = ItemDrop.ItemData.Load(pkg, item, (global::Version.Item)version);
				if (foods.Contains(hash))
				{
					totals[hash] = (totals.TryGetValue(hash, out int t) ? t : 0) + item.m_stack;
				}
			}
			return totals.Count == 0 ? $"none ({count} other item stacks)"
				: string.Join(", ", totals.Select(x => (ZNetScene.instance.GetPrefab(x.Key)?.name ?? x.Key.ToString(CultureInfo.InvariantCulture)) + " " + x.Value));
		}
		catch (Exception ex)
		{
			return "unreadable (" + ex.Message + ")";
		}
	}

	// ServerDevcommands remembers which admin sent the remote command; use it for the centre.
	private static Vector3? CallerPosition()
	{
		Type type = AccessTools.TypeByName("ServerDevcommands.RedirectOutput");
		if (!(type?.GetField("Target", BindingFlags.Static | BindingFlags.Public)?.GetValue(null) is ZRpc rpc))
		{
			return null;
		}
		ZNetPeer peer = ZNet.instance.GetPeers().FirstOrDefault(p => p.m_rpc == rpc);
		return peer?.GetRefPos();
	}

	private static void Print(Terminal output, string text)
	{
		// ServerDevcommands forwards console output to the caller unless it starts with '['.
		(output ?? (Terminal)Console.instance)?.AddString(text);
	}

	private static string Format(Vector3 pos)
	{
		return $"({pos.x:0}, {pos.y:0}, {pos.z:0})";
	}
}
