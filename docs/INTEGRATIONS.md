# Fitting the toolkit to a project's existing lifetimes

`MemoryManager.CreateSceneScope()` assumes two things that a lot of projects do not do: that scenes
are how the project ends things, and that the scope can be created *after* the scene is known.
Neither holds for an Addressables scene load, a bespoke flow manager, an additive UI stack, or a
match that ends without a scene change.

That gap matters more than it looks. **A lifetime the toolkit cannot express is a lifetime someone
hand-rolls beside it** — and two systems that disagree about ownership leak worse than one that never
existed.

---

## If the project uses a DI container

Most Unity studios already express lifetime through VContainer or Zenject. That is what a
`LifetimeScope` and a `Context` *are*.

Adopting `MemoryScope` **as well** creates a second ownership system alongside the first, and the two
can disagree: a container torn down while its memory scope lives leaks everything the scope owns, and
a scope disposed first leaves resolved objects holding released pools. When that happens the blame
lands on whichever package arrived second.

So the memory scope is not a peer of the container's lifetime — it is a **dependent** of it. One
lifetime, and it is the one the project already had.

### VContainer

```csharp
public sealed class LevelScope : LifetimeScope
{
    protected override void Configure(IContainerBuilder builder)
    {
        builder.RegisterMemoryScope("Level");
        builder.Register<EnemySpawner>(Lifetime.Scoped);
    }
}

public sealed class EnemySpawner
{
    private readonly GameObjectPool _pool;
    public EnemySpawner(MemoryScope scope, EnemyConfig config) => _pool = scope.GetPool(config.Prefab);
}
```

`RegisterMemoryScope` creates the scope, registers it for injection, and wires its disposal to the
container's. Both halves are required: **VContainer's `RegisterInstance` does not transfer
ownership** — it only disposes what it creates — so registering the scope and assuming it will be
torn down is the obvious mistake, and it leaks the entire scope silently.

An overload takes a scope you already have, for one created during a load before the container was
built:

```csharp
builder.RegisterMemoryScope(scopeCreatedDuringLoading);
```

### Zenject / Extenject

```csharp
public sealed class LevelInstaller : MonoInstaller
{
    public override void InstallBindings() => Container.BindMemoryScope("Level");
}

// downstream
[Inject] private MemoryScope _scope;
```

Same shape, same reason. `BindMemoryScope` makes two bindings, and they are not the same thing: one
makes the scope injectable, the other puts it in the container's disposal pipeline. Binding only the
first resolves perfectly and leaks all of it — which is why the adapter does both rather than leaving
it to the caller to remember.

### Installing

Both adapters are **optional assemblies with no hard dependency**, using the same version-define
pattern as the Addressables assembly. Install the container and the adapter compiles; do not install
it and the assembly does not exist. Nothing to configure.

| Package | Compiles when |
|---|---|
| `MemoryToolkit.VContainer` | `jp.hadashikick.vcontainer` ≥ 1.13 |
| `MemoryToolkit.Zenject` | `com.svermeulen.extenject` ≥ 9.0 |

Both are verified against the real packages (VContainer 1.16.5, Extenject 9.2.0) in the test suite —
`Tests/Integrations/` holds a test assembly per container, gated on the same define, so they compile
away in a project that has neither.

---

## If the project does not use a container

Three primitives, all on `MemoryScope`, for binding disposal to whatever actually ends the lifetime.

### `AttachTo(GameObject)`

```csharp
_scope = MemoryManager.CreateScope("Level").AttachTo(levelRoot);
```

Dies with the object. Use the one whose destruction genuinely marks the end — a level root, a flow
manager's context object, a container's GameObject.

Attaching a second scope to the same object throws rather than overwriting: a silent overwrite would
drop the first scope's disposal and leak all of it with no symptom at the call site.

### `AttachTo(Scene)`

```csharp
_scope = MemoryManager.CreateScope("Level");
SceneInstance loaded = await Addressables.LoadSceneAsync(key).Task;
_scope.AttachTo(loaded.Scene);
```

Separate from `CreateSceneScope()` because the order is reversed in practice: with Addressables you
create the scope, start the load, and only then hold a `Scene` to attach to.

### `DisposeWhen(subscribe, unsubscribe)`

```csharp
scope.DisposeWhen(
    handler => flow.SessionEnded += handler,
    handler => flow.SessionEnded -= handler);
```

For a lifetime the project already has an event for. It unsubscribes on disposal either way round —
a scope disposed early must not stay alive on an event that outlives it, which is the leak this is
most often used to fix.

### `OnDisposed(Action)`

Notification, not ownership — use `Register` for anything the scope should own, since `OnDisposed`
does not take part in the LIFO teardown order.

It runs **immediately if the scope has already been disposed**. Integration code subscribes to
teardown from outside, so "the thing I am wiring up already ended" is a normal race rather than an
error; silently never firing would leave the subscriber waiting on an event that has been and gone.
