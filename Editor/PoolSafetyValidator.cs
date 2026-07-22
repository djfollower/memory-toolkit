using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using MemoryToolkit.Pooling;
using UnityEditor;
using UnityEngine;

namespace MemoryToolkit.Editor
{
    /// <summary>
    /// Static checks for "can this prefab survive being pooled?".
    ///
    /// Pooling fails in ways that do not look like pooling bugs: a particle
    /// system deletes its own GameObject and the pool serves fake-null; an
    /// <c>OnDestroy</c> that used to do the cleanup silently stops running;
    /// physics state carries over from the previous user. Each of those is
    /// detectable before the first play session, which is the point — teams
    /// abandon pooling after one week of chasing a crash whose cause is three
    /// systems away.
    ///
    /// The checks are necessarily conservative: they read prefab data and type
    /// metadata, not method bodies. A clean report means "nothing statically
    /// disqualifying", not "provably correct" — a script calling
    /// <c>Destroy(gameObject)</c> on itself cannot be seen from here.
    /// </summary>
    public static class PoolSafetyValidator
    {
        public enum Severity
        {
            /// <summary>Pooling this prefab will break. Fix before pooling.</summary>
            Error,

            /// <summary>Pooling changes this behaviour. Confirm it is handled.</summary>
            Warning,

            /// <summary>Worth knowing; not necessarily wrong.</summary>
            Info,
        }

        public readonly struct Issue
        {
            public readonly Severity Severity;
            public readonly string Path;
            public readonly string Message;
            public readonly UnityEngine.Object Context;

            public Issue(Severity severity, string path, string message, UnityEngine.Object context)
            {
                Severity = severity;
                Path = path;
                Message = message;
                Context = context;
            }

            public override string ToString() => $"[{Severity}] {Path}: {Message}";
        }

        private const BindingFlags MessageFlags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>
        /// Appends every issue found on <paramref name="prefab"/> and its
        /// children to <paramref name="results"/>. Does not clear the list.
        /// </summary>
        public static void Validate(GameObject prefab, List<Issue> results)
        {
            if (prefab == null) throw new ArgumentNullException(nameof(prefab));
            if (results == null) throw new ArgumentNullException(nameof(results));

            bool hasAnyPoolable = prefab.GetComponentInChildren<IPoolable>(includeInactive: true) != null;

            var transforms = new List<Transform>();
            prefab.GetComponentsInChildren(includeInactive: true, transforms);

            foreach (Transform t in transforms)
            {
                GameObject go = t.gameObject;
                string path = PathOf(prefab.transform, t);
                bool poolableHere = go.GetComponent<IPoolable>() != null;

                ValidateParticleSystem(go, path, results);
                ValidatePhysics(go, path, poolableHere || hasAnyPoolable, results);
                ValidateScripts(go, path, results);
            }

            if (!hasAnyPoolable)
            {
                results.Add(new Issue(Severity.Info, prefab.name,
                    "No component implements IPoolable. If any per-use state exists (timers, " +
                    "subscriptions, tweens, physics), it will carry over to the next user.",
                    prefab));
            }
        }

        private static void ValidateParticleSystem(GameObject go, string path, List<Issue> results)
        {
            var particles = go.GetComponent<ParticleSystem>();
            if (particles == null) return;

            ParticleSystemStopAction stopAction = particles.main.stopAction;
            if (stopAction == ParticleSystemStopAction.Destroy)
            {
                results.Add(new Issue(Severity.Error, path,
                    "ParticleSystem Stop Action is Destroy: the instance deletes its own GameObject " +
                    "when the effect ends, so the pool will hand out destroyed instances. " +
                    "Set Stop Action to Disable (or Callback) and release it from OnParticleSystemStopped.",
                    go));
            }
            else if (particles.main.loop)
            {
                results.Add(new Issue(Severity.Info, path,
                    "Looping ParticleSystem never stops on its own; something must release it explicitly.",
                    go));
            }
            else if (stopAction == ParticleSystemStopAction.None)
            {
                results.Add(new Issue(Severity.Info, path,
                    "ParticleSystem Stop Action is None. Use Callback and release the instance from " +
                    "OnParticleSystemStopped, or the effect finishes but the instance stays active.",
                    go));
            }
        }

        private static void ValidatePhysics(GameObject go, string path, bool hasPoolableReset, List<Issue> results)
        {
            bool has2D = go.GetComponent<Rigidbody2D>() != null;
            bool has3D = go.GetComponent<Rigidbody>() != null;
            if (!has2D && !has3D) return;

            if (!hasPoolableReset)
            {
                results.Add(new Issue(Severity.Warning, path,
                    $"{(has2D ? "Rigidbody2D" : "Rigidbody")} with no IPoolable on the prefab: velocity, " +
                    "angular velocity, isKinematic and gravity scale persist across reuse. A body frozen " +
                    "by gameplay comes back frozen. Reset them in OnReturnedToPool.",
                    go));
            }
        }

        private static void ValidateScripts(GameObject go, string path, List<Issue> results)
        {
            var components = new List<Component>();
            go.GetComponents(components);

            foreach (Component component in components)
            {
                if (component == null)
                {
                    results.Add(new Issue(Severity.Error, path,
                        "Missing script reference. Instantiating this prefab logs errors per instance.",
                        go));
                    continue;
                }

                if (component is not MonoBehaviour) continue;

                Type type = component.GetType();
                bool isPoolable = component is IPoolable;

                if (DeclaresMessage(type, "OnDestroy"))
                {
                    results.Add(new Issue(isPoolable ? Severity.Info : Severity.Warning, path,
                        $"{type.Name} declares OnDestroy. Pooled instances are released, not destroyed, " +
                        "so it stops running during gameplay and only fires at pool teardown. Move its " +
                        "cleanup — event unsubscribes, tween kills, coroutine stops — to " +
                        "IPoolable.OnReturnedToPool.",
                        go));
                }

                if (!isPoolable && (DeclaresMessage(type, "OnEnable") || DeclaresMessage(type, "OnDisable")))
                {
                    results.Add(new Issue(Severity.Info, path,
                        $"{type.Name} declares OnEnable/OnDisable. Unlike OnDestroy these DO fire on every " +
                        "pool take/return, so any work in them now runs once per reuse rather than once " +
                        "per instance.",
                        go));
                }

                if (DeclaresMessage(type, "Awake") || DeclaresMessage(type, "Start"))
                {
                    results.Add(new Issue(Severity.Info, path,
                        $"{type.Name} declares Awake/Start. These run once per instance, not once per " +
                        "use: initialisation that must happen per use belongs in OnTakenFromPool.",
                        go));
                }
            }
        }

        /// <summary>
        /// True when <paramref name="type"/> or a subclass of MonoBehaviour above
        /// it declares a parameterless method named <paramref name="name"/>.
        /// Unity messages may be private, and base classes count.
        /// </summary>
        private static bool DeclaresMessage(Type type, string name)
        {
            for (Type t = type; t != null && t != typeof(MonoBehaviour); t = t.BaseType)
            {
                foreach (MethodInfo method in t.GetMethods(MessageFlags))
                {
                    if (method.Name == name && method.GetParameters().Length == 0)
                        return true;
                }
            }

            return false;
        }

        private static string PathOf(Transform root, Transform target)
        {
            if (target == root) return root.name;

            var builder = new StringBuilder(target.name);
            for (Transform t = target.parent; t != null && t != root; t = t.parent)
                builder.Insert(0, '/').Insert(0, t.name);
            return builder.Insert(0, '/').Insert(0, root.name).ToString();
        }

        #region Menu

        [MenuItem("Assets/Memory Toolkit/Validate Pool Safety", validate = true)]
        private static bool ValidateSelectionEnabled()
        {
            foreach (UnityEngine.Object obj in Selection.objects)
            {
                if (obj is GameObject) return true;
            }

            return false;
        }

        [MenuItem("Assets/Memory Toolkit/Validate Pool Safety")]
        private static void ValidateSelection()
        {
            var results = new List<Issue>();
            int prefabCount = 0;
            int errors = 0, warnings = 0;

            foreach (UnityEngine.Object obj in Selection.objects)
            {
                if (obj is not GameObject prefab) continue;

                prefabCount++;
                results.Clear();
                Validate(prefab, results);

                if (results.Count == 0)
                {
                    Debug.Log($"[Pool Safety] {prefab.name}: nothing statically disqualifying.", prefab);
                    continue;
                }

                foreach (Issue issue in results)
                {
                    string line = $"[Pool Safety] {issue.Path}: {issue.Message}";
                    switch (issue.Severity)
                    {
                        case Severity.Error:
                            errors++;
                            Debug.LogError(line, issue.Context);
                            break;
                        case Severity.Warning:
                            warnings++;
                            Debug.LogWarning(line, issue.Context);
                            break;
                        default:
                            Debug.Log(line, issue.Context);
                            break;
                    }
                }
            }

            if (prefabCount > 0)
            {
                Debug.Log($"[Pool Safety] Checked {prefabCount} prefab(s): {errors} error(s), {warnings} warning(s). " +
                          "Static checks only — see docs/ADOPTION.md §6 for the full checklist.");
            }
        }

        #endregion
    }
}
