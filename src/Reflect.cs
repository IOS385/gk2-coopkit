using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GK2.CoopKit
{
    /// <summary>
    /// Everything touches the game through reflection on purpose: a game patch that renames a
    /// member should make this plugin report a missing member, not fail to load.
    /// </summary>
    internal static class Reflect
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;

        internal static Type GameType(string name) =>
            CoopKitPlugin.GameAssembly?.GetType(name, throwOnError: false);

        internal static object MainGame() => GetStatic(GameType("MainGame"), "Instance");

        internal static object NetcodeSingleton()
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("Unity.Netcode.NetworkManager", throwOnError: false))
                .FirstOrDefault(t => t != null);
            return GetStatic(type, "Singleton");
        }

        internal static object GetStatic(Type type, string member)
        {
            if (type == null) return null;
            try
            {
                var property = type.GetProperty(member, Any | BindingFlags.Static);
                if (property != null) return property.GetValue(null);
                var field = type.GetField(member, Any | BindingFlags.Static);
                return field?.GetValue(null);
            }
            catch (Exception e)
            {
                return $"<threw {e.GetType().Name}>";
            }
        }

        internal static object Get(object target, string member)
        {
            if (target == null) return null;
            try
            {
                var type = target.GetType();
                var property = type.GetProperty(member, Any | BindingFlags.Instance);
                if (property != null) return property.GetValue(target);
                var field = type.GetField(member, Any | BindingFlags.Instance);
                return field?.GetValue(target);
            }
            catch (Exception e)
            {
                return $"<threw {e.GetType().Name}>";
            }
        }

        internal static int Count(object value) => value is ICollection collection ? collection.Count : -1;

        internal static string TypeNameOf(object value) => value == null ? "null" : value.GetType().Name;

        internal static string Describe(Exception e)
        {
            var inner = (e as TargetInvocationException)?.InnerException ?? e;
            return $"{inner.GetType().Name}: {inner.Message}";
        }

        internal static string LoadedGameScene()
        {
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var name = SceneManager.GetSceneAt(i).name;
                if (name != "Logos" && name != "MainScene") return name;
            }

            return null;
        }

        internal static string[] LocalAddresses()
        {
            try
            {
                return System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName()).AddressList
                    .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    .Select(a => a.ToString())
                    .ToArray();
            }
            catch
            {
                return new string[0];
            }
        }
    }

    internal static class Files
    {
        internal static void Write(string kind, string content, BepInEx.Logging.ManualLogSource log)
        {
            try
            {
                var directory = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "coopkit");
                System.IO.Directory.CreateDirectory(directory);
                var path = System.IO.Path.Combine(directory, $"{kind}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
                System.IO.File.WriteAllText(path, content);
                log.LogInfo($"Written to {path}");
            }
            catch (Exception e)
            {
                log.LogWarning($"Could not write the {kind} file: {e.Message}");
            }
        }
    }
}
