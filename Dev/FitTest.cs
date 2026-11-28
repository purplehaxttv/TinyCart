using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ScalerCore;
using UnityEngine;

namespace TinyCartDev
{
    // Not shipped. Drops the biggest valuables, a health pack and a cosmetic box into the cart and logs sizes.
    static class FitTest
    {
        public static IEnumerator Run(Action<string> say)
        {
            while (UnityEngine.Object.FindObjectOfType<MenuPageMain>() == null) yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForSecondsRealtime(6f);
            try { SemiFunc.MenuActionSingleplayerGame(); } catch (Exception e) { say("start failed " + e); }

            float deadline = Time.realtimeSinceStartup + 180f;
            yield return new WaitForSecondsRealtime(5f);
            while (!(SemiFunc.RunIsLevel() && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated && UnityEngine.Object.FindObjectsOfType<PhysGrabCart>().Length > 0))
            {
                if (Time.realtimeSinceStartup > deadline) { say("FAIL level never generated"); Application.Quit(); yield break; }
                yield return new WaitForSecondsRealtime(1f);
            }
            yield return new WaitForSecondsRealtime(12f);

            PhysGrabCart cart = UnityEngine.Object.FindObjectsOfType<PhysGrabCart>().OrderBy(c => c.isSmallCart ? 1 : 0).First();
            Transform inCart = Traverse.Create(cart).Field<Transform>("inCart").Value;
            Transform me = PlayerAvatar.instance.transform;
            cart.GetComponent<PhysGrabObject>().Teleport(me.position + me.forward * 2.5f + Vector3.up * 0.3f, me.rotation);
            yield return new WaitForSecondsRealtime(3f);
            say($"basket size {inCart.lossyScale} (x,y,z metres)");

            var sizes = new List<KeyValuePair<PrefabRef, float>>();
            foreach (PrefabRef r in REPOLib.Modules.Valuables.AllValuables)
            {
                GameObject prefab = null;
                try { prefab = r.Prefab; } catch { }
                if (!prefab) continue;
                sizes.Add(new KeyValuePair<PrefabRef, float>(r, TinyCart.Plugin.LongestSide(prefab)));
            }
            sizes = sizes.OrderByDescending(p => p.Value).ToList();
            say("valuables measured: " + sizes.Count + "; unmeasurable: " + sizes.Count(p => p.Value <= 0f));
            say("biggest: " + string.Join(", ", sizes.Take(15).Select(p => $"{p.Key.PrefabName}={p.Value:F2}")));
            say("smallest: " + string.Join(", ", sizes.Where(p => p.Value > 0f).Reverse().Take(5).Select(p => $"{p.Key.PrefabName}={p.Value:F2}")));

            var drops = new List<Func<GameObject>>();
            foreach (var p in sizes.Take(4).Concat(sizes.Skip(sizes.Count / 2).Take(1)))
            {
                PrefabRef r = p.Key;
                drops.Add(() => REPOLib.Modules.Valuables.SpawnValuable(r, inCart.position + Vector3.up * 1.2f, inCart.rotation));
            }
            drops.Add(() =>
            {
                Item pack = REPOLib.Modules.Items.AllItems.Where(i => i && i.itemType == SemiFunc.itemType.healthPack).OrderBy(i => i.name).FirstOrDefault();
                say("health pack item: " + (pack ? pack.name : "none"));
                return pack ? REPOLib.Modules.Items.SpawnItem(pack, inCart.position + Vector3.up * 0.6f, Quaternion.identity) : null;
            });
            foreach (ValuableDirector.CosmeticWorldObjectSetup setup in ValuableDirector.instance.cosmeticWorldObjectSetups)
            {
                PrefabRef r = setup.prefab;
                drops.Add(() => REPOLib.Modules.NetworkPrefabs.SpawnNetworkPrefab(r, inCart.position + Vector3.up * 0.6f, Quaternion.identity, 0));
            }

            int shot = 0;
            foreach (Func<GameObject> drop in drops)
            {
                GameObject go = null;
                try { go = drop(); } catch (Exception e) { say("spawn failed " + e.Message); }
                if (!go) { say("spawned nothing"); continue; }
                float natural = TinyCart.Plugin.LongestSide(go);
                Vector3 scale0 = go.transform.localScale;
                string kind = go.GetComponent<ValuableObject>() ? "valuable" : go.GetComponent<CosmeticWorldObject>() ? "cosmetic" : go.GetComponent<ItemAttributes>() ? "item" : "other";
                bool listed = false;
                for (int i = 0; i < 8; i++)
                {
                    yield return new WaitForSecondsRealtime(1f);
                    if (!go) break;
                    listed |= Traverse.Create(cart).Field<List<PhysGrabObject>>("itemsInCart").Value.Contains(go.GetComponent<PhysGrabObject>());
                }
                if (!go) { say("object destroyed while waiting"); continue; }
                float ratio = go.transform.localScale.x / scale0.x;
                Vector3 local = Quaternion.Inverse(inCart.rotation) * (go.transform.position - inCart.position);
                say($"{kind} {go.name}: natural {natural:F2}m, now x{ratio:F2} = {natural * ratio:F2}m, scaled={ScaleManager.IsScaled(go)}, listed={listed}, offset from basket centre {local}");
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "tinycart_fit" + shot++ + ".png"));
                yield return new WaitForSecondsRealtime(0.5f);
                go.GetComponent<PhysGrabObject>().Teleport(me.position - me.forward * (6f + shot * 2f) + Vector3.up, Quaternion.identity);
                yield return new WaitForSecondsRealtime(1f);
            }
            say("DONE");
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }
    }
}
