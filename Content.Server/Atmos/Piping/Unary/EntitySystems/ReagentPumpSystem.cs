using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Atmos.Piping.Unary.Components;
using Content.Server.Construction;
using Content.Server.NodeContainer;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Power.Components;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Piping;
using Content.Shared.Atmos.Piping.Unary.Components;
using JetBrains.Annotations;
using Robust.Server.GameObjects;

namespace Content.Server.Atmos.Piping.Unary.EntitySystems
{
    [UsedImplicitly]
    public sealed class ReagentPumpSystem : EntitySystem
    {
        [Dependency] private readonly UserInterfaceSystem _userInterfaceSystem = default!;
        [Dependency] private readonly PopupSystem _popupSystem = default!;
        [Dependency] private readonly AudioSystem _audioSystem = default!;
        [Dependency] private readonly ItemSlotsSystem _itemSlotsSystem = default!;
        [Dependency] private readonly SolutionContainerSystem _solutionContainerSystem = default!;

        public override void Initialize()
        {
            base.Initialize();
        }

        private void UpdateUiState(ReagentPumpComponent ReagentPump)
        {
            if (!_solutionContainerSystem.TryGetSolution(ReagentPump.Owner, SharedReagentPump.BufferSolutionName, out var bufferSolution))
                return;

            var outputContainer = _itemSlotsSystem.GetItemOrNull(ReagentPump.Owner, SharedReagentPump.OutputSlotName);
            var outputContainerInfo = BuildContainerInfo(outputContainer, ReagentPump);

            var bufferReagents = bufferSolution.Contents;
            var bufferCurrentVolume = bufferSolution.Volume;

            var state = new ReagentPumpBoundUserInterfaceState(outputContainerInfo);
            _userInterfaceSystem.TrySetUiState(ReagentPump.Owner, ReagentPumpUiKey.Key, state);
        }

        private ReagentPumpContainerInfo? BuildContainerInfo(EntityUid? container, ReagentPumpComponent ReagentPump)
        {
            if (container is not { Valid: true })
                return null;

            ClickSound(ReagentPump);

            if (TryComp<SolutionContainerManagerComponent>(container, out var solutions))
                foreach (var solution in (solutions.Solutions)) //will only work on the first iter val
                {
                    var reagents = solution.Value.Contents.Select(reagent => (reagent.ReagentId, reagent.Quantity)).ToList();
                    return new ContainerInfo(Name(container.Value), true, solution.Value.Volume, solution.Value.MaxVolume, reagents);
                }

            return null;
        }

        private void ClickSound(ReagentPumpComponent ReagentPump)
        {
            _audioSystem.PlayPvs(ReagentPump.ClickSound, ReagentPump.Owner, AudioParams.Default.WithVolume(-2f));
        }
    }
}
