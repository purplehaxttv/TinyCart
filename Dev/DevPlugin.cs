using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using ScalerCore;
using UnityEngine;

namespace TinyCartDev
{
    // Not shipped. Starts a solo run, puts valuables and a duck in the cart and logs what happens, then quits.
    [BepInPlugin("purplehaxttv.TinyCartDev", "Tiny Cart Dev", "0.0.1")]
    [BepInDependency("purplehaxttv.TinyCart")]
    [BepInDependency("REPOLib")]
    public class DevPlugin : BaseUnityPlugin
    {
        static ManualLogSource L;

        void Awake()
        {
            L = Logger;
            transform.parent = null;
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            StartCoroutine(Run());
        }

        static void Say(string s) => L.LogInfo("DEVTEST " + s);

        IEnumerator Run()
        {
            while (FindObjectOfType<MenuPageMain>() == null) yield return new WaitForSecondsRealtime(1f);
            Say("main menu up");
            yield return new WaitForSecondsRealtime(6f);
            try { SemiFunc.MenuActionSingleplayerGame(); } catch (Exception e) { Say("start failed " + e); }

            float deadline = Time.realtimeSinceStartup + 180f;
            yield return new WaitForSecondsRealtime(5f);
            while (!(SemiFunc.RunIsLevel() && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated && FindObjectsOfType<PhysGrabCart>().Length > 0))
            {
                if (Time.realtimeSinceStartup > deadline) { Say("FAIL level never generated"); Application.Quit(); yield break; }
                yield return new WaitForSecondsRealtime(1f);
            }
            Say("level generated");
            yield return new WaitForSecondsRealtime(12f);

            PhysGrabCart cart = FindObjectsOfType<PhysGrabCart>().OrderBy(c => c.isSmallCart ? 1 : 0).FirstOrDefault();
            if (!cart) { Say("FAIL no cart"); Application.Quit(); yield break; }
            Transform inCart = Traverse.Create(cart).Field<Transform>("inCart").Value;
            Say($"cart {cart.name} at {cart.transform.position}, nearest player {SemiFunc.PlayerNearestDistance(cart.transform.position):F1}m");

            Transform me = PlayerAvatar.instance.transform;
            cart.GetComponent<PhysGrabObject>().Teleport(me.position + me.forward * 2.5f + Vector3.up * 0.3f, me.rotation);
            yield return new WaitForSecondsRealtime(3f);
            Say($"cart moved, nearest player {SemiFunc.PlayerNearestDistance(cart.transform.position):F1}m");
            List<PhysGrabObject> valuables = FindObjectsOfType<ValuableObject>()
                .Select(v => v.GetComponent<PhysGrabObject>()).Where(p => p)
                .OrderBy(p => p.GetComponent<Rigidbody>().mass).Take(3).ToList();
            var originals = new Dictionary<PhysGrabObject, Vector3>();
            int n = 0;
            foreach (PhysGrabObject v in valuables)
            {
                originals[v] = v.transform.localScale;
                v.Teleport(inCart.position + Vector3.up * 0.2f + inCart.right * (n - 1) * 0.3f, Quaternion.identity);
                n++;
            }

            EnemyRigidbody duckBody = null;
            EnemyParent duckParent = null;
            try
            {
                EnemySetup setup = REPOLib.Modules.Enemies.AllEnemies.FirstOrDefault(e => e && e.name.IndexOf("Duck", StringComparison.OrdinalIgnoreCase) >= 0);
                if (setup)
                {
                    List<EnemyParent> spawned = REPOLib.Modules.Enemies.SpawnEnemy(setup, inCart.position + Vector3.up * 0.5f, Quaternion.identity, false);
                    duckParent = spawned?.FirstOrDefault();
                    Say("spawned " + (duckParent ? duckParent.name + " / " + duckParent.enemyName : "nothing"));
                }
                else Say("no duck setup; enemies: " + string.Join(", ", REPOLib.Modules.Enemies.AllEnemies.Select(e => e.name)));
            }
            catch (Exception e) { Say("duck spawn failed " + e); }

            var extras = new List<EnemyParent>();
            try
            {
                Say("enemies: " + string.Join(", ", REPOLib.Modules.Enemies.AllEnemies.Where(e => e).Select(e => e.name)));
                EnemySetup gnome = REPOLib.Modules.Enemies.AllEnemies.FirstOrDefault(e => e && e.name.IndexOf("Gnome", StringComparison.OrdinalIgnoreCase) >= 0);
                EnemySetup other = REPOLib.Modules.Enemies.AllEnemies.FirstOrDefault(e => e && e.name.IndexOf("Animal", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? REPOLib.Modules.Enemies.AllEnemies.FirstOrDefault(e => e && e.name.IndexOf("Gnome", StringComparison.OrdinalIgnoreCase) < 0 && e.name.IndexOf("Duck", StringComparison.OrdinalIgnoreCase) < 0);
                foreach (EnemySetup s in new[] { gnome, other })
                {
                    if (!s) continue;
                    List<EnemyParent> got = REPOLib.Modules.Enemies.SpawnEnemy(s, inCart.position + Vector3.up * 0.6f, Quaternion.identity, false);
                    if (got != null) extras.AddRange(got.Where(x => x));
                    Say("spawned extra " + s.name + " x" + (got?.Count ?? 0));
                }
            }
            catch (Exception e) { Say("extra spawn failed " + e); }
            StartCoroutine(Extras(extras, inCart));

            Vector3 duckOriginal = Vector3.one;
            for (int i = 0; i < 12; i++)
            {
                yield return new WaitForSecondsRealtime(1f);
                if (duckParent && !duckBody)
                {
                    duckBody = duckParent.GetComponentInChildren<EnemyRigidbody>(true);
                    if (duckBody) duckOriginal = duckBody.transform.localScale;
                }
                if (duckBody && i < 6)
                {
                    PhysGrabObject p = duckBody.GetComponent<PhysGrabObject>();
                    if (p) p.Teleport(inCart.position + Vector3.up * 0.3f, Quaternion.identity);
                }
                Report("in cart t=" + (i + 1), cart, valuables, originals, duckBody, duckOriginal);
                if (i == 5) ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(Paths.BepInExRootPath, "tinycart_in.png"));
            }

            if (valuables.Count > 0)
            {
                PhysGrabObject first = valuables[0];
                first.Teleport(cart.transform.position + cart.transform.forward * 3f + Vector3.up * 0.5f, Quaternion.identity);
                Say("moved " + first.name + " out of the cart");
                for (int i = 0; i < 5; i++)
                {
                    yield return new WaitForSecondsRealtime(1f);
                    Report("one out t=" + (i + 1), cart, valuables, originals, duckBody, duckOriginal);
                }
                first.Teleport(inCart.position + Vector3.up * 0.2f, Quaternion.identity);
                Say("moved it back in");
                for (int i = 0; i < 4; i++)
                {
                    yield return new WaitForSecondsRealtime(1f);
                    Report("back in t=" + (i + 1), cart, valuables, originals, duckBody, duckOriginal);
                }
            }

            GameObject meObject = PlayerAvatar.instance.gameObject;
            for (int i = 0; i < 10; i++)
            {
                if (i < 5) cart.GetComponent<PhysGrabObject>().Teleport(me.position - (inCart.position - cart.transform.position) + Vector3.up * 0.1f, cart.transform.rotation);
                if (i == 5) { cart.GetComponent<PhysGrabObject>().Teleport(me.position + me.forward * 5f + Vector3.up * 0.3f, cart.transform.rotation); Say("cart moved away from player"); }
                yield return new WaitForSecondsRealtime(1f);
                Say($"player t={i + 1} scaled={ScaleManager.IsScaled(meObject)} hp={Traverse.Create(PlayerAvatar.instance.playerHealth).Field<int>("health").Value}");
            }
            Say("DONE");
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        IEnumerator Extras(List<EnemyParent> extras, Transform inCart)
        {
            for (int i = 0; i < 14; i++)
            {
                yield return new WaitForSecondsRealtime(1f);
                var parts = new List<string>();
                foreach (EnemyParent p in extras)
                {
                    if (!p) { parts.Add("(gone)"); continue; }
                    EnemyRigidbody body = p.GetComponentInChildren<EnemyRigidbody>(true);
                    if (!body) { parts.Add(p.enemyName + " nobody"); continue; }
                    if (i < 8 && body.gameObject.activeInHierarchy)
                    {
                        PhysGrabObject pg = body.GetComponent<PhysGrabObject>();
                        bool gnome = p.name.IndexOf("Gnome", StringComparison.OrdinalIgnoreCase) >= 0;
                        Vector3 spot = gnome ? inCart.position + Vector3.up * 0.3f : inCart.position + inCart.right * (inCart.localScale.x / 2f + 0.45f);
                        if (pg && (gnome || i == 4)) { pg.Teleport(spot, Quaternion.identity); if (!gnome) Say("placed " + p.enemyName + " beside the cart"); }
                    }
                    EnemyHealth health = body.enemy ? body.enemy.GetComponent<EnemyHealth>() : null;
                    string hp = health ? Traverse.Create(health).Field<int>("healthCurrent").Value + " dead=" + Traverse.Create(health).Field<bool>("dead").Value : "?";
                    parts.Add($"{p.enemyName} hp={hp} active={body.gameObject.activeInHierarchy} scaled={ScaleManager.IsScaled(body.gameObject)}");
                }
                Say("extras t=" + (i + 1) + " | " + string.Join(" | ", parts));
            }
        }

        static void Report(string tag, PhysGrabCart cart, List<PhysGrabObject> valuables, Dictionary<PhysGrabObject, Vector3> originals, EnemyRigidbody duck, Vector3 duckOriginal)
        {
            var parts = new List<string>();
            List<PhysGrabObject> inCart = Traverse.Create(cart).Field<List<PhysGrabObject>>("itemsInCart").Value;
            parts.Add("cartCount=" + inCart.Count);
            foreach (PhysGrabObject v in valuables)
            {
                if (!v) { parts.Add("(destroyed)"); continue; }
                float ratio = v.transform.localScale.x / originals[v].x;
                ValuableObject vo = v.GetComponent<ValuableObject>();
                parts.Add($"{v.name}: x{ratio:F2} scaled={ScaleManager.IsScaled(v.gameObject)} mass={v.GetComponent<Rigidbody>().mass:F2} ${Traverse.Create(vo).Field<float>("dollarValueCurrent").Value:F0} listed={inCart.Contains(v)}");
            }
            if (duck)
            {
                EnemyHealth health = duck.enemy ? duck.enemy.GetComponent<EnemyHealth>() : null;
                string hp = health ? Traverse.Create(health).Field<int>("healthCurrent").Value.ToString() : "?";
                parts.Add($"DUCK x{duck.transform.localScale.x / duckOriginal.x:F2} scaled={ScaleManager.IsScaled(duck.gameObject)} hp={hp} active={duck.gameObject.activeInHierarchy} listed={inCart.Contains(duck.GetComponent<PhysGrabObject>())}");
            }
            else parts.Add("DUCK none");
            Say(tag + " | " + string.Join(" | ", parts));
        }
    }
}
