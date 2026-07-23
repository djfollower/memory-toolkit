# Budgets: the numbers as an asset

Warm-up counts, pool sizes, arena capacities and heap ceilings used to be literals in an installer.
This makes them a `MemoryBudget` asset, tiered by device class.

Two reasons, and neither is tidiness.

**A single warm-up number is wrong on two of any three targets.** A count tuned on the programmer's
phone under-warms the minimum-spec device and wastes memory on the high end. Tiering is the thing
that makes one budget correct everywhere, and it cannot be expressed as a literal without three
literals and a branch.

**The person who knows what a level should cost is usually not the person who can edit an installer.**
A tech artist can open an asset. Numbers in code are also invisible to the build farm — a budget can
be asserted against ([`CI.md`](CI.md)) and written back from a measured session, which is what keeps
it from rotting.

---

## The asset

**Create > Memory Toolkit > Memory Budget.**

One entry per scope, matched on `MemoryScope.Name` — `Permanent`, a scene name, or a manual scope
like `Match`. Each entry lists its pools and, optionally, an arena size. Scope-level ceilings sit at
the bottom of the asset.

```csharp
// Level load, replacing a hand-written installer:
_scope = MemoryManager.CreateSceneScope();
MemoryBudget.ApplyResult result = budget.ApplyTo(_scope);

// Boot, once, before anything touches MemoryManager.FrameScratch:
budget.ApplyGlobals();
```

`ApplyTo` matches on scope name and does nothing when there is no entry — an unbudgeted scope is
normal during adoption, not an error.

`ApplyGlobals` sets `FrameScratchCapacityBytes` and `LowMemoryKeepPerPool`. Call it before anything
reads `MemoryManager.FrameScratch`: reading that property allocates the arena at whatever size is
configured *at that moment*, and it is not resized afterwards.

---

## Tiers

`Low` / `Medium` / `High`. Three, for the same reason there are three lifetime tiers — a fourth
creates an authoring decision nobody makes consistently. They map onto what a QA matrix already has:
the minimum-spec device, the median phone, and everything comfortable.

Every number is a `TieredInt`, three fields. **Fill in `High` and leave the rest at zero to get one
number everywhere** — a zero means "same as the tier above". A partially filled row is therefore
always coherent, and the common case (this prefab does not need tiering) costs one field.

```
Warmup   High 32   Medium 16   Low 8     → 32 / 16 / 8
Warmup   High 32   Medium  0   Low 0     → 32 / 32 / 32
Warmup   High 32   Medium 16   Low 0     → 32 / 16 / 16
```

### Which tier is this device?

`DeviceTier.Current`, resolved once and cached — a tier that could change between two pools in the
same scene would produce a half-Low, half-High configuration that matches nothing anyone tested.

The default provider reads `SystemInfo.systemMemorySize` and is deliberately crude. A real studio has
a device database keyed by model string; supply it during boot:

```csharp
DeviceTier.Provider = new OurDeviceDatabaseTierProvider();
```

`DeviceTier.Override(tier)` forces a tier for the session — for QA builds that need to test the
minimum-spec configuration on whatever hardware is on the desk, and for tests.

---

## The reference footgun

**A direct `GameObject` reference in a budget pins that prefab — and its meshes, materials and
textures — for as long as the budget is loaded.** A budget listing every level's prefabs by direct
reference loads every level's content at boot: a memory tool that costs more memory than it saves.

The rule:

- **Permanent-tier prefabs → direct reference.** They are resident anyway; the reference costs
  nothing extra.
- **Everything else → `AddressableKey`,** a string this asset cannot accidentally load.

The CI gate flags a direct reference in a non-Permanent scope as a warning. Because the runtime
assembly has no Addressables dependency and will not grow one to make an API look complete,
`ApplyTo` warms the direct references and hands the keyed entries back:

```csharp
MemoryBudget.ApplyResult result = budget.ApplyTo(_scope);
foreach (PoolBudget pending in result.PendingAddressables)
{
    GameObject prefab = await _scope.LoadAssetAsync<GameObject>(pending.AddressableKey).Task;
    _scope.Warmup(prefab, pending.Warmup.Current, pending.MaxSize.Current);
}
```

---

## Closing the loop: Apply measured peaks

A warm-up count is a measurement — the Timeline's peak active over a representative session. Until
now the last step of that measurement was a human reading a number off a chart and typing it into an
installer, which is exactly where the loop broke: done once, at adoption, never again.

1. **Window > Analysis > Memory Toolkit Inspector**, press **Record**.
2. Play a representative session. A peak is only as good as the session that produced it — a
   two-minute run through the tutorial sizes a pool for the tutorial.
3. Select the budget asset, choose the tier to write into, press **Apply measured peaks**.

It writes each recorded pool's peak into the matching entry's `Warmup`, matched on scope name and
prefab name, and raises `MaxSize` where it would otherwise sit below the warm-up. Entries with no
recording are left alone.

Recorded pools with **no** budget entry are reported in the console rather than added, because which
scope owns a pool is a decision, not a measurement — the whole premise of
[`ADOPTION.md`](ADOPTION.md) §2 is that assigning lifetimes is the part a human does.

---

## Ceilings

`ManagedHeapCeilingMb` and the rest are **asserted, not enforced**. Nothing at runtime refuses an
allocation for being over budget — that would turn a memory problem into a crash at a worse moment.
They exist so the dynamic CI gate can fail a nightly run that drifted, which is the point at which
someone can actually act.
