using System.Linq;
using Content.Shared.Administration.Logs;
using Content.Shared.Alert;
using Content.Shared.CombatMode;
using Content.Shared.Damage.Components;
using Content.Shared.Damage.Events;
using Content.Shared.Database;
using Content.Shared.Crawling;
using Content.Shared.Effects;
using Content.Shared.IdentityManagement;
using Content.Shared.Popups;
using Content.Shared.Projectiles;
using Content.Shared.Rejuvenate;
using Content.Shared.Rounding;
using Content.Shared.Stunnable;
using Content.Shared.Throwing;
using Content.Shared.Weapons.Melee.Events;
using JetBrains.Annotations;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Shared.Damage.Systems;

public sealed partial class StaminaSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly AlertsSystem _alerts = default!;
    [Dependency] private readonly CrawlingSystem _crawling = default!;
    [Dependency] private readonly MetaDataSystem _metadata = default!;
    [Dependency] private readonly SharedColorFlashEffectSystem _color = default!;
    [Dependency] private readonly SharedStunSystem _stunSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;

    /// <summary>
    /// How much of a buffer is there between the stun duration and when stuns can be re-applied.
    /// </summary>
    private static readonly TimeSpan StamCritBufferTime = TimeSpan.FromSeconds(3f);

    public override void Initialize()
    {
        base.Initialize();

        InitializeModifier();

        SubscribeLocalEvent<StaminaComponent, ComponentStartup>(OnStartup);
        SubscribeLocalEvent<StaminaComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<StaminaComponent, AfterAutoHandleStateEvent>(OnStamHandleState);
        SubscribeLocalEvent<StaminaComponent, DisarmedEvent>(OnDisarmed);
        SubscribeLocalEvent<StaminaComponent, RejuvenateEvent>(OnRejuvenate);

        SubscribeLocalEvent<StaminaDamageOnEmbedComponent, EmbedEvent>(OnProjectileEmbed);

        SubscribeLocalEvent<StaminaDamageOnCollideComponent, ProjectileHitEvent>(OnProjectileHit);
        SubscribeLocalEvent<StaminaDamageOnCollideComponent, ThrowDoHitEvent>(OnThrowHit);

        SubscribeLocalEvent<StaminaDamageOnHitComponent, MeleeHitEvent>(OnMeleeHit);
    }

    private void OnStamHandleState(EntityUid uid, StaminaComponent component, ref AfterAutoHandleStateEvent args)
    {
        if (component.Critical)
            EnterStamCrit(uid, component, false);
        else
        {

            if (component.SoftStaminaDamage > 0f || component.HardStaminaDamage > 0f)
                EnsureComp<ActiveStaminaComponent>(uid);

            if (component.Crawling)
                EnterStamCrit(uid, component, true);
            else
                ExitStamCrit(uid, component, true);
        }
    }

    private void OnShutdown(EntityUid uid, StaminaComponent component, ComponentShutdown args)
    {
        if (MetaData(uid).EntityLifeStage < EntityLifeStage.Terminating)
        {
            RemCompDeferred<ActiveStaminaComponent>(uid);
        }
        _alerts.ClearAlert(uid, component.StaminaAlert);
    }

    private void OnStartup(EntityUid uid, StaminaComponent component, ComponentStartup args)
    {
        SetStaminaAlert(uid, component);
    }

    [PublicAPI]
    public float GetStaminaDamage(EntityUid uid, StaminaComponent? component = null, bool soft = true)
    {
        if (!Resolve(uid, ref component))
            return 0f;

        float stamina;

        switch (soft)
        {
            case true:
                stamina = component.SoftStaminaDamage;
                break;

            case false:
                stamina = component.HardStaminaDamage;
                break;
        }

        var curTime = _timing.CurTime;
        var pauseTime = _metadata.GetPauseTime(uid);
        return MathF.Max(0f, stamina - MathF.Max(0f, (float) (curTime - (component.NextUpdate + pauseTime)).TotalSeconds * component.Decay));
    }

    private void OnRejuvenate(EntityUid uid, StaminaComponent component, RejuvenateEvent args)
    {
        if (component.HardStaminaDamage >= component.CritThreshold)
        {
            ExitStamCrit(uid, component, false);
        }

        if (component.SoftStaminaDamage >= component.CritThreshold)
        {
            ExitStamCrit(uid, component, true);
        }

        component.SoftStaminaDamage = 0;
        component.HardStaminaDamage = 0;
        RemComp<ActiveStaminaComponent>(uid);
        SetStaminaAlert(uid, component);
        Dirty(uid, component);
    }

    private void OnDisarmed(EntityUid uid, StaminaComponent component, DisarmedEvent args)
    {
        if (args.Handled)
            return;

        if (component.Crawling)
        {
            args.Handled = true;
            return;
        }

        var damage = args.PushProbability * component.CritThreshold;
        TakeStaminaDamage(uid, damage, component, source: args.Source, soft: true);

        args.PopupPrefix = "disarm-action-shove-";
        args.IsStunned = component.Crawling;

        args.Handled = true;
    }

    private void OnMeleeHit(EntityUid uid, StaminaDamageOnHitComponent component, MeleeHitEvent args)
    {
        if (!args.IsHit ||
            !args.HitEntities.Any() ||
            component.Damage <= 0f)
        {
            return;
        }

        var ev = new StaminaDamageOnHitAttemptEvent();
        RaiseLocalEvent(uid, ref ev);
        if (ev.Cancelled)
            return;

        var stamQuery = GetEntityQuery<StaminaComponent>();
        var toHit = new List<(EntityUid Entity, StaminaComponent Component)>();

        // Split stamina damage between all eligible targets.
        foreach (var ent in args.HitEntities)
        {
            if (!stamQuery.TryGetComponent(ent, out var stam))
                continue;

            toHit.Add((ent, stam));
        }

        var hitEvent = new StaminaMeleeHitEvent(toHit);
        RaiseLocalEvent(uid, hitEvent);

        if (hitEvent.Handled)
            return;

        var damage = component.Damage;

        damage *= hitEvent.Multiplier;

        damage += hitEvent.FlatModifier;

        foreach (var (ent, comp) in toHit)
        {
            TakeStaminaDamage(ent, damage / toHit.Count, comp, source: args.User, args.Weapon, component.Soft, sound: component.Sound);
        }
    }

    private void OnProjectileHit(EntityUid uid, StaminaDamageOnCollideComponent component, ref ProjectileHitEvent args)
    {
        OnCollide(uid, component, args.Target);
    }

    private void OnProjectileEmbed(EntityUid uid, StaminaDamageOnEmbedComponent component, ref EmbedEvent args)
    {
        if (!TryComp<StaminaComponent>(args.Embedded, out var stamina))
            return;

        TakeStaminaDamage(args.Embedded, component.Damage, stamina, uid, soft: component.Soft);
    }

    private void OnThrowHit(EntityUid uid, StaminaDamageOnCollideComponent component, ThrowDoHitEvent args)
    {
        OnCollide(uid, component, args.Target);
    }

    private void OnCollide(EntityUid uid, StaminaDamageOnCollideComponent component, EntityUid target)
    {
        // you can't inflict stamina damage on things with no stamina component
        // this prevents stun batons from using up charges when throwing it at lockers or lights
        if (!HasComp<StaminaComponent>(target))
            return;

        var ev = new StaminaDamageOnHitAttemptEvent();
        RaiseLocalEvent(uid, ref ev);
        if (ev.Cancelled)
            return;

        TakeStaminaDamage(target, component.Damage, source: uid, soft: component.Soft, sound: component.Sound);
    }

    private void SetStaminaAlert(EntityUid uid, StaminaComponent? component = null)
    {
        if (!Resolve(uid, ref component, false) || component.Deleted)
            return;

        double severity = ContentHelpers.RoundToLevels(MathF.Max(0f, component.CritThreshold - component.SoftStaminaDamage), component.CritThreshold, 7) / 2;

        if (component.HardStaminaDamage > 0f)
            severity += ContentHelpers.RoundToLevels(MathF.Max(0f, component.CritThreshold - component.HardStaminaDamage), component.CritThreshold, 7) / 2;

        severity = Math.Round(severity);

        _alerts.ShowAlert(uid, component.StaminaAlert, (short) severity);
    }

    /// <summary>
    /// Tries to take stamina damage without raising the entity over the crit threshold.
    /// </summary>
    public bool TryTakeStamina(EntityUid uid, float value, StaminaComponent? component = null, EntityUid? source = null, EntityUid? with = null, bool soft = true)
    {
        // Something that has no Stamina component automatically passes stamina checks
        if (!Resolve(uid, ref component, false))
            return true;

        if (component.HardStaminaDamage + value > component.CritThreshold || component.Critical)
            return false;

        // start dealing hard stam now
        if (component.SoftStaminaDamage + value > component.CritThreshold || component.Crawling)
            soft = false;

        TakeStaminaDamage(uid, value, component, source, with, visual: false, soft);
        return true;
    }

    public void TakeStaminaDamage(EntityUid uid, float value, StaminaComponent? component = null,
        EntityUid? source = null, EntityUid? with = null, bool visual = true, bool soft = true, SoundSpecifier? sound = null)
    {
        if (!Resolve(uid, ref component, false))
            return;

        var ev = new BeforeStaminaDamageEvent(value);
        RaiseLocalEvent(uid, ref ev);
        if (ev.Cancelled)
            return;

        // Have we already reached the point of max stamina damage?
        if (component.Critical)
            return;

        // if softstam is reached deal hard stun instead
        if (soft && component.SoftStaminaDamage >= component.CritThreshold)
            soft = false;

        float oldDamage;
        float currentDamage;

        switch (soft)
        {
            case true:
                oldDamage = component.SoftStaminaDamage;
                component.SoftStaminaDamage = MathF.Max(0f, component.SoftStaminaDamage + value);
                currentDamage = component.SoftStaminaDamage;
                break;

            case false:
                oldDamage = component.HardStaminaDamage;
                component.HardStaminaDamage = MathF.Max(0f, component.HardStaminaDamage + value);
                currentDamage = component.HardStaminaDamage;
                component.SoftStaminaDamage = component.CritThreshold;
                break;
        }

        // Reset the decay cooldown upon taking damage.
        if (oldDamage < currentDamage)
        {
            var nextUpdate = _timing.CurTime + TimeSpan.FromSeconds(component.Cooldown);

            if (component.NextUpdate < nextUpdate)
                component.NextUpdate = nextUpdate;
        }

        var slowdownThreshold = component.CritThreshold / 2f;

        // If we go above n% then apply slowdown
        if (oldDamage < slowdownThreshold &&
            component.HardStaminaDamage < 0 &&
            component.SoftStaminaDamage > slowdownThreshold)
        {
            _stunSystem.TrySlowdown(uid, TimeSpan.FromSeconds(3), true, 0.8f, 0.8f);
        }

        SetStaminaAlert(uid, component);

        if (!component.Crawling)
        {
            if (component.SoftStaminaDamage >= component.CritThreshold)
            {
                EnterStamCrit(uid, component, true);
            }
        }
        else
        {
            if (component.SoftStaminaDamage < component.CritThreshold)
            {
                ExitStamCrit(uid, component, true);
            }
        }

        if (!component.Critical)
        {
            if (component.HardStaminaDamage >= component.CritThreshold)
            {
                EnterStamCrit(uid, component, false);
            }
        }
        else
        {
            if (component.HardStaminaDamage < component.CritThreshold)
            {
                ExitStamCrit(uid, component, false);
            }
        }

        EnsureComp<ActiveStaminaComponent>(uid);
        Dirty(uid, component);

        if (value <= 0)
            return;
        if (source != null)
        {
            _adminLogger.Add(LogType.Stamina, $"{ToPrettyString(source.Value):user} caused {value} stamina damage to {ToPrettyString(uid):target}{(with != null ? $" using {ToPrettyString(with.Value):using}" : "")}");
        }
        else
        {
            _adminLogger.Add(LogType.Stamina, $"{ToPrettyString(uid):target} took {value} stamina damage");
        }

        if (visual)
        {
            _color.RaiseEffect(Color.Aqua, new List<EntityUid>() { uid }, Filter.Pvs(uid, entityManager: EntityManager));
        }

        if (_net.IsServer)
        {
            _audio.PlayPvs(sound, uid);
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (!_timing.IsFirstTimePredicted)
            return;

        var stamQuery = GetEntityQuery<StaminaComponent>();
        var query = EntityQueryEnumerator<ActiveStaminaComponent>();
        var curTime = _timing.CurTime;

        while (query.MoveNext(out var uid, out _))
        {
            // Just in case we have active but not stamina we'll check and account for it.
            if (!stamQuery.TryGetComponent(uid, out var comp) ||
                comp.SoftStaminaDamage <= 0f && !comp.Crawling &&
                comp.HardStaminaDamage <= 0f && !comp.Critical)
            {
                RemComp<ActiveStaminaComponent>(uid);
                continue;
            }

            // Shouldn't need to consider paused time as we're only iterating non-paused stamina components.
            var nextUpdate = comp.NextUpdate;

            if (nextUpdate > curTime)
                continue;

            // We were in crit so come out of it and continue.
            if (comp.Critical && comp.HardStaminaDamage <= comp.CritThreshold)
            {
                ExitStamCrit(uid, comp, false);
                continue;
            }

            bool soft = comp.HardStaminaDamage <= 0;

            if (soft && comp.Crawling && comp.SoftStaminaDamage <= comp.CritThreshold)
            {
                ExitStamCrit(uid, comp, true);
                continue;
            }

            comp.NextUpdate += TimeSpan.FromSeconds(1f);
            TakeStaminaDamage(uid, comp.Decay * -1, comp, soft: soft);
            Dirty(uid, comp);
        }
    }

    private void EnterStamCrit(EntityUid uid, StaminaComponent? component = null, bool soft = true)
    {
        if (!Resolve(uid, ref component) ||
            component.Critical)
        {
            return;
        }

        // To make the difference between a stun and a stamcrit clear
        // TODO: Mask?

        switch (soft)
        {
            case true:
            {
                if (!TryComp<CrawlerComponent>(uid, out var crawlerComp))
                    goto case false;

                if (!HasComp<CrawlingComponent>(uid))
                {
                    _crawling.SetCrawling(uid, crawlerComp, true);
                }

                component.Critical = false;
                component.Crawling = true;
                component.HardStaminaDamage = 0f;
                break;
            }
            case false:
            {
                if (TryComp<CrawlerComponent>(uid, out var crawlerComp) && HasComp<CrawlingComponent>(uid))
                {
                    _crawling.SetCrawling(uid, crawlerComp, false);
                }

                component.Critical = true;
                component.Crawling = false;
                component.HardStaminaDamage = component.CritThreshold;
                _stunSystem.TryParalyze(uid, component.StunTime, true);
                break;
            }
        }

        // Give them buffer before being able to be re-stunned
        component.NextUpdate = _timing.CurTime + component.StunTime + StamCritBufferTime;
        EnsureComp<ActiveStaminaComponent>(uid);
        Dirty(uid, component);
        _adminLogger.Add(LogType.Stamina, LogImpact.Medium, $"{ToPrettyString(uid):user} entered stamina crit");
    }

    private void ExitStamCrit(EntityUid uid, StaminaComponent? component = null, bool soft = true)
    {
        if (!Resolve(uid, ref component) ||
            !(component.Crawling || component.Critical))
        {
            return;
        }

        switch (soft)
        {
            case true:
            {
                component.Crawling = false;
                component.HardStaminaDamage = 0f;
                RemComp<ActiveStaminaComponent>(uid);

                if (TryComp<CrawlerComponent>(uid, out var crawlerComp) && HasComp<CrawlingComponent>(uid))
                {
                    _crawling.SetCrawling(uid, crawlerComp, false);
                }
                break;
            }
            case false:
            {
                component.SoftStaminaDamage = component.CritThreshold;
                EnterStamCrit(uid, component, true);
                break;
            }

        }

        component.NextUpdate = _timing.CurTime;
        SetStaminaAlert(uid, component);
        Dirty(uid, component);
        _adminLogger.Add(LogType.Stamina, LogImpact.Low, $"{ToPrettyString(uid):user} recovered from stamina crit");
    }
}

/// <summary>
///     Raised before stamina damage is dealt to allow other systems to cancel it.
/// </summary>
[ByRefEvent]
public record struct BeforeStaminaDamageEvent(float Value, bool Cancelled = false);
