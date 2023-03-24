using Content.Shared.Atmos;
using Content.Shared.Atmos.Piping.Unary.Components;
using Robust.Shared.Audio;

namespace Content.Server.Atmos.Piping.Unary.Components
{
    [RegisterComponent]
    public sealed class ReagentPumpComponent : Component
    {
        [DataField("clickSound")] public SoundSpecifier ClickSound { get; set; } = new SoundPathSpecifier("/Audio/Machines/machine_switch.ogg");

        [DataField("mode"), ViewVariables(VVAccess.ReadWrite)]
        public ReagentPumpMode Mode = ReagentPumpMode.Transfer;
    }
}
