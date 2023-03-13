using Content.Server.Power.Components;
using Content.Shared.Tag;
using Robust.Shared.Map;
using Robust.Shared.Physics.Components;
using Robust.Shared.Physics.Systems;

namespace Content.Server.Mining;

[RegisterComponent]
public class AirJetComponent : Component
{
    [DataField("rate")]
    [ViewVariables(VVAccess.ReadWrite)]
    public float Rate = 1f; //< Ores per second

    [ViewVariables(VVAccess.ReadWrite)]
    public float Accum = 0f;

    [DataField("force")]
    [ViewVariables(VVAccess.ReadWrite)]
    public float Force = 60f;

    [DataField("targetDist")]
    [ViewVariables(VVAccess.ReadWrite)]
    public float TargetDist = 1f;

    [DataField("range")]
    [ViewVariables(VVAccess.ReadWrite)]
    public float Range = 0.2f;
}

public class AirJetSystem : EntitySystem
{
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;
    [Dependency] private readonly TagSystem _tagSystem = default!;

    public override void Update(float frameTime)
    {
        foreach (var (comp, apc) in EntityManager.EntityQuery<AirJetComponent, ApcPowerReceiverComponent>())
        {
            if (!apc.Powered)
            {
                // Not powered, don't do anything
                comp.Accum = 0;
                continue;
            }
            
            comp.Accum += frameTime;
            if (comp.Accum < comp.Rate)
                continue;

            comp.Accum -= comp.Rate;

            // do thing here
            var xformQuery = GetEntityQuery<TransformComponent>();
            if (!xformQuery.TryGetComponent(comp.Owner, out var myXform))
            {
                // We need our own transform in order to move things
                continue;
            }

            var forceVec = (myXform.WorldRotation - Math.PI/2).ToVec();
            var tileInFront = myXform.WorldPosition + forceVec*comp.TargetDist;
            var coord = new MapCoordinates(tileInFront, myXform.MapID);
            foreach (var uid in _lookup.GetEntitiesInRange(coord, comp.Range))
            {
                // skip self
                if (uid == comp.Owner)
                    continue;

                if (_tagSystem.HasTag(uid, "Ore"))
                {
                    Logger.InfoS("jet", "Moved one entity at " + tileInFront.ToString());

                    var force = forceVec * comp.Force;
                    _physics.ApplyLinearImpulse(uid, force);

                    // Only process one
                    break;
                }
            }
        }
    }
}
