using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx;
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
// Progress lives in server memory. Writing it into client-owned ZDOs would be lost to the owner's
// updates, so it is only copied into the ZDOs (without a revision bump) just before the world saves.
[BepInPlugin(Guid, "ServerTaming", Version)]
public sealed class ServerTamingPlugin : BaseUnityPlugin
{
	public const string Guid = "local.servertaming";

	public const string Version = "1.0.0";

	private static readonly int KeyTameLeft = "ServerTaming_tameLeft".GetStableHashCode();

	private static readonly int KeyFedUntil = "ServerTaming_fedUntil".GetStableHashCode();

	private static readonly int KeyLove = "ServerTaming_love".GetStableHashCode();

	private static readonly int KeyPregnant = "ServerTaming_pregnant".GetStableHashCode();

	private static ServerTamingPlugin _instance;

	private ConfigEntry<float> _tickSeconds;

	private ConfigEntry<float> _feedRadius;

	private ConfigEntry<bool> _requireCalm;

	private ConfigEntry<float> _messageRange;

	private ConfigEntry<bool> _progressMessages;

	private ConfigEntry<int> _maxTameStars;

	private ConfigEntry<float> _breedCheckSeconds;

	private ConfigEntry<float> _loveChance;

	private ConfigEntry<int> _requiredLovePoints;

	private ConfigEntry<float> _partnerRange;

	private ConfigEntry<float> _populationRange;

	private ConfigEntry<int> _maxCreatures;

	private readonly List<Species> _species = new List<Species>();

	private readonly Dictionary<int, Species> _speciesByPrefab = new Dictionary<int, Species>();

	private readonly HashSet<int> _foodPrefabs = new HashSet<int>();

	private readonly Dictionary<ZDOID, State> _states = new Dictionary<ZDOID, State>();

	private readonly List<ZDO> _sectorObjects = new List<ZDO>();

	private readonly HashSet<ZDOID> _seen = new HashSet<ZDOID>();

	private readonly List<ZDO> _animals = new List<ZDO>();

	private readonly List<ZDO> _foods = new List<ZDO>();

	private readonly HashSet<ZDOID> _eaten = new HashSet<ZDOID>();

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

		public int Hash;

		public string Name;

		public HashSet<int> FoodHashes = new HashSet<int>();

		public ZNetView View;
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
		_tickSeconds = Config.Bind("General", "TickSeconds", 5f, "How often (seconds) animals near players are checked.");
		_feedRadius = Config.Bind("General", "FeedRadius", 8f, "Animals eat matching food lying within this many metres. They do not walk to it, so keep them penned with the food inside.");
		_requireCalm = Config.Bind("General", "RequireCalm", true, "Taming and breeding pause while the animal is alerted (fleeing or fighting), like vanilla.");
		_messageRange = Config.Bind("General", "MessageRange", 30f, "Players within this many metres see taming and birth messages.");
		_progressMessages = Config.Bind("General", "ProgressMessages", true, "Show taming progress at 25/50/75% to nearby players.");
		_maxTameStars = Config.Bind("General", "MaxTameStars", -1, "Highest star count that can be tamed; -1 means any (including the 3+ star creatures from level mods such as ServersideQoL CreatureLevelUp). Offspring keep their parent's stars.");
		_breedCheckSeconds = Config.Bind("Breeding", "CheckSeconds", 30f, "How often each fed tamed animal tries to gain a love point.");
		_loveChance = Config.Bind("Breeding", "LoveChance", 0.5f, "Chance (0-1) of gaining a love point on each check when a partner is near.");
		_requiredLovePoints = Config.Bind("Breeding", "RequiredLovePoints", 4, "Love points needed to become pregnant.");
		_partnerRange = Config.Bind("Breeding", "PartnerRange", 5f, "A tamed partner of the same kind must be within this many metres.");
		_populationRange = Config.Bind("Breeding", "PopulationRange", 15f, "Radius used to count animals of the same kind for MaxCreatures.");
		_maxCreatures = Config.Bind("Breeding", "MaxCreatures", 8, "No new love points when this many animals of the same kind are within PopulationRange.");
		AddSpecies("Deer", "Raspberry,Blueberries,Cloudberry,Carrot,Turnip,Onion,Mushroom,MushroomYellow", 25f, 5f, 10f);
		AddSpecies("Neck", "FishRaw", 25f, 5f, 10f);
		new Terminal.ConsoleCommand("tamestatus", "[radius] - show server taming/breeding progress of animals near you; use through 'server tamestatus'", Status);
		new Harmony(Guid).PatchAll(typeof(SavePatch));
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
			PregnancyMinutes = Config.Bind(prefab, "PregnancyMinutes", pregnancy, "Minutes from pregnancy to birth.")
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
		Collect();
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
				TryEat(zdo, s, st, now);
			}
			bool fed = now < st.FedUntil;
			if (!tamed)
			{
				UpdateTaming(zdo, s, st, fed && calm, dt);
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
			}
		}
	}

	private State GetState(ZDO zdo)
	{
		if (!_states.TryGetValue(zdo.m_uid, out State st))
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

	private void TryEat(ZDO animal, Species s, State st, double now)
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
			return;
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
		st.FedUntil = now + s.FedMinutes.Value * 60.0;
	}

	private void UpdateTaming(ZDO zdo, Species s, State st, bool progressing, float dt)
	{
		float total = Mathf.Max(1f, s.TamingMinutes.Value * 60f);
		if (st.TameLeft < 0f || st.TameLeft > total)
		{
			st.TameLeft = total;
		}
		if (progressing && st.TameLeft > 0f)
		{
			int before = Step(st.TameLeft, total);
			st.TameLeft = Mathf.Max(0f, st.TameLeft - dt);
			int after = Step(st.TameLeft, total);
			if (_progressMessages.Value && after > before && after < 4)
			{
				Message(zdo.GetPosition(), MessageHud.MessageType.TopLeft, $"{s.Name}: taming {after * 25}%");
			}
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
		Vector3 pos = zdo.GetPosition();
		float population2 = _populationRange.Value * _populationRange.Value;
		float partner2 = _partnerRange.Value * _partnerRange.Value;
		int count = 0;
		bool partner = false;
		foreach (ZDO other in _animals)
		{
			if (other.GetPrefab() != s.Hash)
			{
				continue;
			}
			float d2 = (other.GetPosition() - pos).sqrMagnitude;
			if (d2 <= population2)
			{
				count++;
			}
			if (other != zdo && d2 <= partner2 && other.GetBool(ZDOVars.s_tamed))
			{
				partner = true;
			}
		}
		if (!partner || count >= _maxCreatures.Value)
		{
			return;
		}
		if (++st.Love >= Mathf.Max(1, _requiredLovePoints.Value))
		{
			st.Love = 0;
			st.Pregnant = now;
		}
	}

	// Creates the offspring directly as a ZDO (like ZNetView.Awake does) so the server does not need
	// the area loaded; the nearest client takes ownership and instantiates a vanilla tamed adult.
	private void Birth(ZDO parent, Species s)
	{
		float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
		Vector3 pos = parent.GetPosition() + new Vector3(Mathf.Cos(angle) * 1.5f, 0.5f, Mathf.Sin(angle) * 1.5f);
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
		Collect();
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
				float total = Mathf.Max(1f, s.TamingMinutes.Value * 60f);
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
