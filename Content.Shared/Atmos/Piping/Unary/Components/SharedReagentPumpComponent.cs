using Robust.Shared.Serialization;

namespace Content.Shared.Atmos.Piping.Unary.Components;

[Serializable]
[NetSerializable]
public enum ReagentPumpUiKey
{
    Key
}


[Serializable]
[NetSerializable]
public sealed class GasReagentPumpToggleMessage : BoundUserInterfaceMessage
{
}


[Serializable]
[NetSerializable]
public sealed class GasReagentPumpBoundUserInterfaceState : BoundUserInterfaceState
{
    

    public GasReagentPumpBoundUserInterfaceState(ReagentPumpMode mode)
    {
        
        Mode = mode;
    }
}
