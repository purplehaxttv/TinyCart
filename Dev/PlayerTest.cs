using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using BepInEx.Configuration;
using HarmonyLib;
using ScalerCore;
using UnityEngine;

namespace TinyCartDev
{
    // Not shipped. Puts the cart under the player with different grow chances and times the return to normal size.
    static class PlayerTest
    {
        static ConfigFile config;

        static ConfigEntryBase Entry(string key) => config[new ConfigDefinition("Players", key)];

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

            foreach (EnemyParent enemy in UnityEngine.Object.FindObjectsOfType<EnemyParent>())
            {
                try
                {
                    Traverse.Create(enemy).Method("SpawnedTimerSet", 0f).GetValue();
                    Traverse.Create(enemy).Method("DespawnedTimerSet", 600f, false).GetValue();
                }
                catch (Exception e) { say("could not send away " + enemy.name + ": " + e.Message); }
            }

            config = Chainloader.PluginInfos["purplehaxttv.TinyCart"].Instance.Config;
            ConfigEntryBase chance = Entry("Chance to grow instead");
            ConfigEntryBase timer = Entry("Seconds until normal size");
            object chanceBefore = chance.BoxedValue, timerBefore = timer.BoxedValue;
            say($"defaults: chance={chanceBefore} grow scale={Entry("Grow scale").BoxedValue} shrink scale={Entry("Scale").BoxedValue} timer={timerBefore}");

            PhysGrabCart cart = UnityEngine.Object.FindObjectsOfType<PhysGrabCart>().OrderBy(c => c.isSmallCart ? 1 : 0).First();
            PhysGrabObject cartBody = cart.GetComponent<PhysGrabObject>();
            Transform inCart = Traverse.Create(cart).Field<Transform>("inCart").Value;
            PlayerAvatar avatar = PlayerAvatar.instance;
            GameObject me = avatar.gameObject;

            Func<float> factor = () =>
            {
                ScaleController c = ScaleManager.GetController(me);
                return c != null && c.IsScaled ? c.CurrentOptions.Factor : 1f;
            };
            Func<int> hp = () => Traverse.Create(avatar.playerHealth).Field<int>("health").Value;

            var cases = new List<KeyValuePair<float, float>>
            {
                new KeyValuePair<float, float>(100f, 6f),
                new KeyValuePair<float, float>(0f, 3f),
            };
            for (int i = 0; i < 10; i++) cases.Add(new KeyValuePair<float, float>(50f, 0.5f));

            int grew = 0, shrank = 0;
            foreach (KeyValuePair<float, float> c in cases)
            {
                chance.BoxedValue = c.Key;
                timer.BoxedValue = c.Value;

                float start = Time.realtimeSinceStartup;
                while (Mathf.Approximately(factor(), 1f) && Time.realtimeSinceStartup - start < 8f)
                {
                    cartBody.Teleport(me.transform.position - (inCart.position - cart.transform.position) + Vector3.up * 0.1f, cart.transform.rotation);
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                float got = factor();
                if (Mathf.Approximately(got, 1f)) { say($"CASE chance={c.Key} timer={c.Value}: FAIL player never changed size"); continue; }
                if (got > 1f) grew++; else shrank++;

                // stay in the cart longer than the timer: the size must hold while inside
                float hold = c.Value + 2f;
                start = Time.realtimeSinceStartup;
                while (Time.realtimeSinceStartup - start < hold)
                {
                    cartBody.Teleport(me.transform.position - (inCart.position - cart.transform.position) + Vector3.up * 0.1f, cart.transform.rotation);
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                bool held = Mathf.Approximately(factor(), got);
                float height = me.transform.localScale.y;

                cartBody.Teleport(me.transform.position + me.transform.forward * 6f + Vector3.up * 0.3f, cart.transform.rotation);
                float left = Time.realtimeSinceStartup;
                while (!Mathf.Approximately(factor(), 1f) && Time.realtimeSinceStartup - left < c.Value + 15f)
                    yield return new WaitForSecondsRealtime(0.1f);
                float took = Time.realtimeSinceStartup - left;
                say($"CASE chance={c.Key} timer={c.Value}: factor x{got:F2} avatar scale y={height:F2} heldInCart={held} normalAfter={took:F1}s back={Mathf.Approximately(factor(), 1f)} hp={hp()}");
                yield return new WaitForSecondsRealtime(2.6f);
            }
            say($"SUMMARY grew={grew} shrank={shrank} of {cases.Count} (first forced grow, second forced shrink, ten at 50%)");

            chance.BoxedValue = chanceBefore;
            timer.BoxedValue = timerBefore;
            say("DONE");
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }
    }
}
