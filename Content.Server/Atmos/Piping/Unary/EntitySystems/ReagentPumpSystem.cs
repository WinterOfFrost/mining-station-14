using System.Linq;
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
using Content.Server.Popups;
using Content.Shared.Containers.ItemSlots;
using Content.Server.Chemistry.EntitySystems;
using Content.Server.Chemistry.Components.SolutionManager;
using Content.Shared.Audio;
using Robust.Shared.Audio;
using Content.Shared.FixedPoint;
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Shared.Containers;

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

            SubscribeLocalEvent<ReagentPumpComponent, ComponentStartup>((_, comp, _) => UpdateUiState(comp));
            SubscribeLocalEvent<ReagentPumpComponent, SolutionChangedEvent>((_, comp, _) => UpdateUiState(comp));
            SubscribeLocalEvent<ReagentPumpComponent, EntInsertedIntoContainerMessage>((_, comp, _) => UpdateUiState(comp));
            SubscribeLocalEvent<ReagentPumpComponent, EntRemovedFromContainerMessage>((_, comp, _) => UpdateUiState(comp));

            SubscribeLocalEvent<ReagentPumpComponent, BoundUIOpenedEvent>((_, comp, _) => UpdateUiState(comp));

            SubscribeLocalEvent<ReagentPumpComponent, ReagentPumpSetModeMessage>(OnSetModeMessage);
            SubscribeLocalEvent<ReagentPumpComponent, ReagentPumpReagentAmountButtonMessage>(OnReagentButtonMessage);
        }

        private void UpdateUiState(ReagentPumpComponent ReagentPump)
        {
            if (!_solutionContainerSystem.TryGetSolution(ReagentPump.Owner, SharedReagentPump.BufferSolutionName, out var bufferSolution))
                return;

            var outputContainer = _itemSlotsSystem.GetItemOrNull(ReagentPump.Owner, SharedReagentPump.OutputSlotName);
            var outputContainerInfo = BuildContainerInfo(outputContainer, ReagentPump);

            var bufferReagents = bufferSolution.Contents;
            var bufferCurrentVolume = bufferSolution.Volume;

            //var pipeNetReagents = ;

            var state = new ReagentPumpBoundUserInterfaceState(ReagentPump.Mode,outputContainerInfo,bufferReagents,bufferCurrentVolume);
            _userInterfaceSystem.TrySetUiState(ReagentPump.Owner, ReagentPumpUiKey.Key, state);
        }

        private void OnSetModeMessage(EntityUid uid, ReagentPumpComponent ReagentPump, ReagentPumpSetModeMessage message)
        {
            // Ensure the mode is valid, either Transfer or Discard.
            if (!Enum.IsDefined(typeof(ReagentPumpMode), message.ReagentPumpMode))
                return;

            ReagentPump.Mode = message.ReagentPumpMode;
            UpdateUiState(ReagentPump);
            ClickSound(ReagentPump);
        }

        private void OnReagentButtonMessage(EntityUid uid, ReagentPumpComponent ReagentPump, ReagentPumpReagentAmountButtonMessage message)
        {
            // Ensure the amount corresponds to one of the reagent amount buttons.
            if (!Enum.IsDefined(typeof(ReagentPumpReagentAmount), message.Amount))
                return;

            switch (ReagentPump.Mode)
            {
                case ReagentPumpMode.Transfer:
                    TransferReagents(ReagentPump, message.ReagentId, message.Amount.GetFixedPoint(), message.FromBuffer);
                    break;
                case ReagentPumpMode.Discard:
                    DiscardReagents(ReagentPump, message.ReagentId, message.Amount.GetFixedPoint(),message.FromBuffer);
                    break;
                default:
                    // Invalid mode.
                    return;
            }

            ClickSound(ReagentPump);
        }

        private void TransferReagents(ReagentPumpComponent ReagentPump, string reagentId, FixedPoint2 amount, bool fromBuffer)
        {
            var container = _itemSlotsSystem.GetItemOrNull(ReagentPump.Owner, SharedReagentPump.OutputSlotName);
            if (container is null ||
                !TryComp<SolutionContainerManagerComponent>(container.Value, out var containerSolution) ||
                !_solutionContainerSystem.TryGetSolution(ReagentPump.Owner, SharedReagentPump.BufferSolutionName, out var bufferSolution))
                return;

            if (containerSolution is null)
                return;

            if (fromBuffer) // Buffer to container
            {
                foreach (var solution in (containerSolution.Solutions)) //TODO make this better...
                {
                    amount = FixedPoint2.Min(amount, solution.Value.AvailableVolume);
                    amount = bufferSolution.RemoveReagent(reagentId, amount);
                    _solutionContainerSystem.TryAddReagent(container.Value, solution.Value, reagentId, amount, out var _);
                }
            }
            else // Container to buffer
            {
                foreach (var solution in (containerSolution.Solutions)) //TODO make this better...
                {
                    amount = FixedPoint2.Min(amount, solution.Value.GetReagentQuantity(reagentId));
                    _solutionContainerSystem.TryRemoveReagent(container.Value, solution.Value, reagentId, amount);
                    bufferSolution.AddReagent(reagentId, amount);
                }
            }

            UpdateUiState(ReagentPump);
        }

        private void DiscardReagents(ReagentPumpComponent ReagentPump, string reagentId, FixedPoint2 amount, bool fromBuffer)
        {

            if (fromBuffer)
            {
                if (_solutionContainerSystem.TryGetSolution(ReagentPump.Owner, SharedReagentPump.BufferSolutionName, out var bufferSolution))
                    bufferSolution.RemoveReagent(reagentId, amount);
                else
                    return;
            }
            else
            {
                var container = _itemSlotsSystem.GetItemOrNull(ReagentPump.Owner, SharedReagentPump.OutputSlotName);
                if (container is not null &&
                    _solutionContainerSystem.TryGetFitsInDispenser(container.Value, out var containerSolution))
                {
                    _solutionContainerSystem.TryRemoveReagent(container.Value, containerSolution, reagentId, amount);
                }
                else
                    return;
            }

            UpdateUiState(ReagentPump);
        }

        private ReagentPumpContainerInfo? BuildContainerInfo(EntityUid? container, ReagentPumpComponent ReagentPump)
        {
            if (container is not { Valid: true })
                return null;

            if (TryComp<SolutionContainerManagerComponent>(container, out var solutions))
                foreach (var solution in (solutions.Solutions)) //will only work on the first iter val
                {
                    var reagents = solution.Value.Contents.Select(reagent => (reagent.ReagentId, reagent.Quantity)).ToList();
                    return new ReagentPumpContainerInfo(Name(container.Value), true, solution.Value.Volume, solution.Value.MaxVolume, reagents);
                }

            return null;
        }

        private void ClickSound(ReagentPumpComponent ReagentPump)
        {
            _audioSystem.PlayPvs(ReagentPump.ClickSound, ReagentPump.Owner, AudioParams.Default.WithVolume(-2f));
        }
    }
}
