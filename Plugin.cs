using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using ScalerCore;
using UnityEngine;

namespace TinyCart
{
    [BepInPlugin(Guid, "Tiny Cart", "1.1.0")]
    [BepInDependency("Vippy.ScalerCore")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "purplehaxttv.TinyCart";

        const float TickSeconds = 0.25f;
        const float CartRescanSeconds = 2f;
        const float ReshrinkCooldown = 2f;

        sealed class Tracked
        {
            public GameObject Object;
            public float LastSeen;
            public bool Enemy;
            public bool Player;
        }

        static readonly AccessTools.FieldRef<PhysGrabCart, List<PhysGrabObject>> ItemsInCart =
            AccessTools.FieldRefAccess<PhysGrabCart, List<PhysGrabObject>>("itemsInCart");

        static readonly AccessTools.FieldRef<EnemyHealth, int> HealthCurrent =
            AccessTools.FieldRefAccess<EnemyHealth, int>("healthCurrent");

        static readonly AccessTools.FieldRef<EnemyHealth, bool> HealthDead =
            AccessTools.FieldRefAccess<EnemyHealth, bool>("dead");

        static readonly AccessTools.FieldRef<PhysGrabCart, Transform> InCart =
            AccessTools.FieldRefAccess<PhysGrabCart, Transform>("inCart");

        static ManualLogSource Log;

        ConfigEntry<float> valuableScale;
        ConfigEntry<bool> shrinkItems;
        ConfigEntry<float> itemScale;
        ConfigEntry<bool> lighterWhenSmall;
        ConfigEntry<bool> growBack;
        ConfigEntry<float> growBackDelay;
        ConfigEntry<float> shrinkSpeed;
        ConfigEntry<bool> activeInShop;
        ConfigEntry<string> enemyNames;
        ConfigEntry<float> enemyScale;
        ConfigEntry<bool> enemyGrowBack;
        ConfigEntry<bool> enemyGrowWhenHurt;
        ConfigEntry<bool> shrinkPlayers;
        ConfigEntry<float> playerScale;
        ConfigEntry<bool> playerGrowWhenHurt;
        ConfigEntry<bool> killEnemies;
        ConfigEntry<string> sparedEnemies;
        ConfigEntry<float> killReach;
        ConfigEntry<float> maxSize;
        ConfigEntry<bool> shrinkCosmetics;
        ConfigEntry<bool> verbose;

        readonly Dictionary<int, Tracked> tracked = new Dictionary<int, Tracked>();
        readonly Dictionary<int, float> cooldownUntil = new Dictionary<int, float>();
        readonly Dictionary<int, float> killRetryAt = new Dictionary<int, float>();
        readonly List<int> scratch = new List<int>();
        PhysGrabCart[] carts = new PhysGrabCart[0];
        float nextTick;
        float nextCartScan;

        void Awake()
        {
            Log = Logger;
            transform.parent = null;
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            valuableScale = Config.Bind("Valuables", "Scale", 0.4f,
                new ConfigDescription("Size of a valuable while it sits in a cart (1 = normal size).", new AcceptableValueRange<float>(0.1f, 1f)));
            lighterWhenSmall = Config.Bind("Valuables", "Lighter when small", true,
                "Shrunk things also weigh less, so a full cart is easier to push.");
            shrinkItems = Config.Bind("Items", "Shrink shop items", true,
                "Also shrink bought items (weapons, grenades, health packs and so on) in a cart.");
            itemScale = Config.Bind("Items", "Scale", 0.4f,
                new ConfigDescription("Size of a bought item while it sits in a cart.", new AcceptableValueRange<float>(0.1f, 1f)));
            growBack = Config.Bind("General", "Grow back when removed", true,
                "Things return to normal size after they have been out of every cart for the delay below. Off = they stay small for the rest of the level.");
            growBackDelay = Config.Bind("General", "Grow back delay", 1.5f,
                new ConfigDescription("Seconds something must be out of the cart before it grows back. Stops things flickering between sizes at the cart's edge.", new AcceptableValueRange<float>(0.5f, 30f)));
            shrinkSpeed = Config.Bind("General", "Shrink speed", 2f,
                new ConfigDescription("How fast the size change plays.", new AcceptableValueRange<float>(0.25f, 10f)));
            activeInShop = Config.Bind("General", "Active in shop", false,
                "Shrink things placed in the cart while in the shop.");
            enemyNames = Config.Bind("Enemies", "Enemies to shrink", "Duck,Apex Predator,Gnome",
                "Comma separated list of enemy names that shrink when they end up in a cart. Add =size to give one its own size, for example Duck=0.7; others use Scale below. Empty = no enemies. * = every enemy. Only matters for enemies the cart does not kill.");
            // 0.3 put the duck partly under the floor; move untouched configs to the new default.
            if (enemyNames.Value == "Duck=0.3,Apex Predator=0.3,Gnome") enemyNames.Value = (string)enemyNames.DefaultValue;
            enemyScale = Config.Bind("Enemies", "Scale", 0.5f,
                new ConfigDescription("Size of a shrunk enemy.", new AcceptableValueRange<float>(0.1f, 1f)));
            enemyGrowBack = Config.Bind("Enemies", "Grow back when out of cart", false,
                "Off = a shrunk enemy stays small for the rest of the level.");
            enemyGrowWhenHurt = Config.Bind("Enemies", "Grow back when hurt", false,
                "A shrunk enemy returns to normal size when it takes damage.");
            shrinkPlayers = Config.Bind("Players", "Shrink players in cart", true,
                "A player standing or lying in a cart shrinks, and always grows back after leaving it.");
            playerScale = Config.Bind("Players", "Scale", 0.5f,
                new ConfigDescription("Size of a player while in a cart.", new AcceptableValueRange<float>(0.1f, 1f)));
            playerGrowWhenHurt = Config.Bind("Players", "Grow back when hurt", true,
                "A shrunk player returns to normal size when they take damage.");
            killEnemies = Config.Bind("Enemies", "Kill enemies in cart", true,
                "An enemy that ends up in a cart dies, unless it is on the spared list below.");
            sparedEnemies = Config.Bind("Enemies", "Enemies spared", "Duck,Apex Predator,Gnome",
                "Comma separated list of enemy names the cart never kills. * = spare every enemy.");
            killReach = Config.Bind("Enemies", "Kill reach", 0.3f,
                new ConfigDescription("How far outside the cart's basket an enemy still counts as hit, in metres. 0 = only enemies inside the basket die.", new AcceptableValueRange<float>(0f, 1.5f)));
            maxSize = Config.Bind("General", "Largest size in cart", 0.8f,
                new ConfigDescription("Longest side, in metres, a valuable, item or cosmetic box may have in a cart. Anything still longer than this after the normal shrink is shrunk further until it fits. Set it to 10 to switch this off.", new AcceptableValueRange<float>(0.3f, 10f)));
            shrinkCosmetics = Config.Bind("Cosmetics", "Shrink cosmetic boxes", true,
                "Also shrink the cosmetic boxes found in levels. They use the valuables scale.");

            // 1.0 shipped with 0.5; move configs still on that default to the smaller one.
            ConfigEntry<int> settingsVersion = Config.Bind("Debug", "Settings version", 1, "Used by the mod to update old defaults. Leave it alone.");
            if (settingsVersion.Value < 2)
            {
                if (Mathf.Approximately(valuableScale.Value, 0.5f)) valuableScale.Value = (float)valuableScale.DefaultValue;
                if (Mathf.Approximately(itemScale.Value, 0.5f)) itemScale.Value = (float)itemScale.DefaultValue;
                settingsVersion.Value = 2;
            }
            verbose = Config.Bind("Debug", "Verbose log", false, "Log every shrink and grow.");

            Log.LogInfo("Tiny Cart 1.1.0 loaded");
        }

        void Update()
        {
            float now = Time.unscaledTime;
            if (now < nextTick) return;
            nextTick = now + TickSeconds;

            try
            {
                Tick(now);
            }
            catch (Exception e)
            {
                Log.LogError($"Tick failed: {e}");
                nextTick = now + 5f;
            }
        }

        void Tick(float now)
        {
            if (!SemiFunc.IsMasterClientOrSingleplayer() || LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated)
            {
                if (tracked.Count > 0 && (LevelGenerator.Instance == null || !LevelGenerator.Instance.Generated))
                {
                    tracked.Clear();
                    cooldownUntil.Clear();
                    killRetryAt.Clear();
                }
                return;
            }

            if (now >= nextCartScan)
            {
                nextCartScan = now + CartRescanSeconds;
                carts = FindObjectsByType<PhysGrabCart>(FindObjectsSortMode.None);
            }

            bool shrinkAllowed = activeInShop.Value || !SemiFunc.RunIsShop();
            foreach (PhysGrabCart cart in carts)
            {
                if (!cart || !cart.isActiveAndEnabled) continue;
                KillTouching(cart, now);
                List<PhysGrabObject> items = ItemsInCart(cart);
                if (items == null) continue;
                for (int i = 0; i < items.Count; i++)
                {
                    PhysGrabObject item = items[i];
                    if (!item) continue;
                    if (TryKill(item.gameObject, now)) continue;
                    PlayerTumble tumble = item.GetComponent<PlayerTumble>();
                    if (tumble)
                    {
                        if (tumble.playerAvatar) SeenPlayer(tumble.playerAvatar, now, true);
                        continue;
                    }
                    Seen(item.gameObject, now, shrinkAllowed);
                }
            }

            if (shrinkPlayers.Value) ScanStandingPlayers(now);

            scratch.Clear();
            foreach (KeyValuePair<int, Tracked> pair in tracked)
            {
                Tracked t = pair.Value;
                if (!t.Object)
                {
                    scratch.Add(pair.Key);
                    continue;
                }
                if (!ScaleManager.IsScaled(t.Object))
                {
                    scratch.Add(pair.Key);
                    cooldownUntil[pair.Key] = now + ReshrinkCooldown;
                    continue;
                }
                bool mayGrow = t.Player || (t.Enemy ? enemyGrowBack.Value : growBack.Value);
                if (mayGrow && now - t.LastSeen >= growBackDelay.Value)
                {
                    Restore(t.Object);
                    scratch.Add(pair.Key);
                    cooldownUntil[pair.Key] = now + ReshrinkCooldown;
                }
            }
            foreach (int id in scratch) tracked.Remove(id);
        }

        void ScanStandingPlayers(float now)
        {
            List<PlayerAvatar> players = SemiFunc.PlayerGetAll();
            if (players == null) return;
            foreach (PlayerAvatar player in players)
            {
                if (!player || !player.gameObject.activeInHierarchy) continue;
                Vector3 feet = player.transform.position + Vector3.up * 0.05f;
                foreach (PhysGrabCart cart in carts)
                {
                    if (!cart || !cart.isActiveAndEnabled || cart.isSmallCart) continue;
                    Transform box = InCart(cart);
                    if (!box || !OverFloor(feet, box, 1f)) continue;
                    SeenPlayer(player, now, OverFloor(feet, box, 0.7f));
                    break;
                }
            }
        }

        static bool OverFloor(Vector3 point, Transform box, float horizontalScale)
        {
            Vector3 local = Quaternion.Inverse(box.rotation) * (point - box.position);
            Vector3 half = box.localScale * 0.5f;
            float floor = -Mathf.Abs(half.y);
            return Mathf.Abs(local.x) <= Mathf.Abs(half.x) * horizontalScale
                && Mathf.Abs(local.z) <= Mathf.Abs(half.z) * horizontalScale
                && local.y >= floor - 0.25f && local.y <= floor + 1.4f;
        }

        void SeenPlayer(PlayerAvatar player, float now, bool mayShrink)
        {
            if (!shrinkPlayers.Value) return;
            GameObject target = player.gameObject;
            int id = target.GetInstanceID();
            if (tracked.TryGetValue(id, out Tracked existing))
            {
                existing.LastSeen = now;
                return;
            }
            if (!mayShrink) return;
            if (cooldownUntil.TryGetValue(id, out float until))
            {
                if (now < until) return;
                cooldownUntil.Remove(id);
            }
            if (ScaleManager.IsScaled(target)) return;

            ScaleOptions options = ScaleOptions.Default;
            options.Factor = playerScale.Value;
            options.Duration = 0f;
            options.Speed = shrinkSpeed.Value;
            options.RestoreSpeed = shrinkSpeed.Value;
            options.AllowedTargets = ScaleTargets.Players;
            options.SuppressImpactFlash = true;
            options.SuppressCameraShake = true;
            options.IgnoreBonkExpand = !playerGrowWhenHurt.Value;
            if (options.Factor >= 0.999f) return;

            if (!Apply(target, options))
            {
                cooldownUntil[id] = now + ReshrinkCooldown;
                return;
            }
            tracked[id] = new Tracked { Object = target, LastSeen = now, Player = true };
            if (verbose.Value) Log.LogInfo($"Shrunk player {player.name}");
        }

        static bool Apply(GameObject target, ScaleOptions options)
        {
            bool native = ScaleManager.NativeSync;
            try
            {
                ScaleManager.NativeSync = false;
                return ScaleManager.ApplyIfNotScaled(target, options);
            }
            finally
            {
                ScaleManager.NativeSync = native;
            }
        }

        void KillTouching(PhysGrabCart cart, float now)
        {
            if (!killEnemies.Value) return;
            Transform box = InCart(cart);
            if (!box) return;
            Collider[] hits = Physics.OverlapBox(box.position, box.localScale / 2f + Vector3.one * killReach.Value, box.rotation);
            foreach (Collider hit in hits)
            {
                EnemyRigidbody body = hit.GetComponentInParent<EnemyRigidbody>();
                if (body) TryKill(body.gameObject, now);
            }
        }

        bool TryKill(GameObject target, float now)
        {
            if (!killEnemies.Value) return false;
            EnemyRigidbody body = target.GetComponent<EnemyRigidbody>();
            if (!body || !body.enemy) return false;
            if (Listed(target, sparedEnemies.Value, out _)) return false;

            int id = target.GetInstanceID();
            if (killRetryAt.TryGetValue(id, out float retry) && now < retry) return true;
            killRetryAt[id] = now + 1f;

            EnemyHealth health = body.enemy.GetComponent<EnemyHealth>();
            if (!health) return true;
            int current = HealthCurrent(health);
            if (HealthDead(health) || current <= 0) return true;

            health.Hurt(Math.Max(current, health.health) + 1, Vector3.up);
            if (verbose.Value) Log.LogInfo($"Cart killed {EnemyLabel(target)}");
            return true;
        }

        static string EnemyLabel(GameObject target)
        {
            EnemyParent parent = target.GetComponentInParent<EnemyParent>();
            return parent ? parent.enemyName : target.name;
        }

        void Seen(GameObject target, float now, bool shrinkAllowed)
        {
            int id = target.GetInstanceID();
            if (tracked.TryGetValue(id, out Tracked existing))
            {
                existing.LastSeen = now;
                return;
            }
            if (!shrinkAllowed) return;
            if (cooldownUntil.TryGetValue(id, out float until))
            {
                if (now < until) return;
                cooldownUntil.Remove(id);
            }
            if (ScaleManager.IsScaled(target)) return;
            if (!Classify(target, out ScaleOptions options, out bool enemy)) return;

            bool native = ScaleManager.NativeSync;
            bool applied;
            try
            {
                ScaleManager.NativeSync = false;
                applied = ScaleManager.ApplyIfNotScaled(target, options);
            }
            finally
            {
                ScaleManager.NativeSync = native;
            }

            if (!applied)
            {
                cooldownUntil[id] = now + ReshrinkCooldown;
                return;
            }
            tracked[id] = new Tracked { Object = target, LastSeen = now, Enemy = enemy };
            if (verbose.Value) Log.LogInfo($"Shrunk {target.name} to x{options.Factor:F2}");
        }

        void Restore(GameObject target)
        {
            bool native = ScaleManager.NativeSync;
            try
            {
                ScaleManager.NativeSync = false;
                ScaleManager.ForceRestore(target);
            }
            finally
            {
                ScaleManager.NativeSync = native;
            }
            if (verbose.Value) Log.LogInfo($"Restored {target.name}");
        }

        bool Classify(GameObject target, out ScaleOptions options, out bool enemy)
        {
            options = ScaleOptions.Default;
            options.Duration = 0f;
            options.Speed = shrinkSpeed.Value;
            options.RestoreSpeed = shrinkSpeed.Value;
            options.SuppressImpactFlash = true;
            options.SuppressCameraShake = true;
            options.SuppressValueDropExpand = true;
            options.PreserveMass = !lighterWhenSmall.Value;
            enemy = false;

            if (target.GetComponent<PhysGrabCart>() || target.GetComponent<PlayerTumble>() || target.GetComponent<PlayerDeathHead>()) return false;
            if (target.GetComponent<ItemCartCannonMain>() || target.GetComponent<ItemCartLaser>()) return false;

            if (target.GetComponent<EnemyRigidbody>())
            {
                if (!Listed(target, enemyNames.Value, out float own)) return false;
                enemy = true;
                options.Factor = own > 0f ? Mathf.Clamp(own, 0.1f, 1f) : enemyScale.Value;
                options.AllowedTargets = ScaleTargets.Enemies;
                options.IgnoreBonkExpand = !enemyGrowWhenHurt.Value;
                options.PreserveMass = false;
                return true;
            }

            float wanted;
            if (target.GetComponent<ValuableObject>()) wanted = valuableScale.Value;
            else if (target.GetComponent<CosmeticWorldObject>())
            {
                if (!shrinkCosmetics.Value) return false;
                wanted = valuableScale.Value;
            }
            else if (target.GetComponent<ItemAttributes>())
            {
                if (!shrinkItems.Value) return false;
                wanted = itemScale.Value;
            }
            else return false;

            options.Factor = FitFactor(target, wanted, maxSize.Value);
            options.AllowedTargets = ScaleTargets.Valuables | ScaleTargets.Items;
            return options.Factor < 0.999f;
        }

        // The wanted scale, or a smaller one when the thing would still be longer than the limit.
        public static float FitFactor(GameObject target, float wanted, float limit)
        {
            float longest = LongestSide(target);
            if (longest <= 0f || longest * wanted <= limit) return wanted;
            return Mathf.Clamp(limit / longest, 0.1f, wanted);
        }

        // Longest side of the solid part of an object, measured along its own axes so the result
        // does not depend on how it happens to be lying.
        public static float LongestSide(GameObject target)
        {
            Transform root = target.transform;
            bool any = false;
            Vector3 min = Vector3.zero, max = Vector3.zero;
            foreach (Collider collider in target.GetComponentsInChildren<Collider>())
            {
                if (collider.isTrigger) continue;
                Vector3 centre, half;
                if (collider is BoxCollider box) { centre = box.center; half = box.size * 0.5f; }
                else if (collider is SphereCollider sphere) { centre = sphere.center; half = Vector3.one * sphere.radius; }
                else if (collider is CapsuleCollider capsule)
                {
                    centre = capsule.center;
                    half = Vector3.one * capsule.radius;
                    half[capsule.direction] = Mathf.Max(capsule.height * 0.5f, capsule.radius);
                }
                else if (collider is MeshCollider mesh && mesh.sharedMesh) { centre = mesh.sharedMesh.bounds.center; half = mesh.sharedMesh.bounds.extents; }
                else continue;

                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 local = centre + new Vector3(
                        (corner & 1) == 0 ? -half.x : half.x,
                        (corner & 2) == 0 ? -half.y : half.y,
                        (corner & 4) == 0 ? -half.z : half.z);
                    Vector3 point = Vector3.Scale(root.InverseTransformPoint(collider.transform.TransformPoint(local)), root.lossyScale);
                    if (!any) { min = max = point; any = true; }
                    else { min = Vector3.Min(min, point); max = Vector3.Max(max, point); }
                }
            }
            if (!any) return 0f;
            Vector3 size = max - min;
            return Mathf.Max(Mathf.Abs(size.x), Mathf.Abs(size.y), Mathf.Abs(size.z));
        }

        static bool Listed(GameObject target, string list, out float size)
        {
            size = 0f;
            if (string.IsNullOrWhiteSpace(list)) return false;

            EnemyParent parent = target.GetComponentInParent<EnemyParent>();
            string enemyName = parent ? parent.enemyName ?? "" : "";
            string objectName = parent ? parent.gameObject.name : target.name;

            foreach (string raw in list.Split(','))
            {
                string wanted = raw.Trim();
                float entrySize = 0f;
                int eq = wanted.IndexOf('=');
                if (eq >= 0)
                {
                    float.TryParse(wanted.Substring(eq + 1).Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out entrySize);
                    wanted = wanted.Substring(0, eq).Trim();
                }
                if (wanted.Length == 0) continue;
                if (wanted == "*") { size = entrySize; return true; }
                if (enemyName.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) { size = entrySize; return true; }
                if (objectName.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) { size = entrySize; return true; }
            }
            return false;
        }
    }
}
