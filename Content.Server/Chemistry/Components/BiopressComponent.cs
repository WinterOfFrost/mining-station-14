using Content.Shared.Chemistry.Components;
using Content.Shared.Chemistry;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.Audio;
using Robust.Shared.Containers;

namespace Content.Server.Chemistry.Components
{
    [RegisterComponent]
    public sealed class BiopressComponent : Component
    {
        /// <summary>
        /// Is the machine actively doing something and can't be used right now?
        /// </summary>
        public bool Busy;

        //YAML serialization vars
        [ViewVariables(VVAccess.ReadWrite)] [DataField("workTime")] public int WorkTime = 3500; //3.5 seconds, completely arbitrary for now.
        [DataField("clickSound")] public SoundSpecifier ClickSound { get; set; } = new SoundPathSpecifier("/Audio/Machines/machine_switch.ogg");
        [DataField("grindSound")] public SoundSpecifier GrindSound { get; set; } = new SoundPathSpecifier("/Audio/Machines/blender.ogg");

        [DataField("mode"), ViewVariables(VVAccess.ReadWrite)]
        public BiopressMode Mode = BiopressMode.Transfer;
    }
}
