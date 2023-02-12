using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Content.Shared.Administration;
using Content.Server.Chemistry.Components;
using Content.Server.Chemistry.Components.SolutionManager;
using Content.Server.Popups;
using Content.Server.Storage.Components;
using Content.Server.Storage.EntitySystems;
using Content.Shared.Chemistry;
using Content.Shared.Chemistry.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Chemistry.Reagent;
using Content.Shared.Chemistry.Reaction;
using Content.Shared.FixedPoint;
using JetBrains.Annotations;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Utility;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Robust.Shared.Prototypes;

namespace Content.Server.Chemistry.EntitySystems
{

    /// <summary>
    /// Contains all the server-side logic for Biopresss.
    /// <seealso cref="BiopressComponent"/>
    /// </summary>
    [UsedImplicitly]
    public sealed class BiopressSystem : EntitySystem
    {
        [Dependency] private readonly PopupSystem _popupSystem = default!;
        [Dependency] private readonly AudioSystem _audioSystem = default!;
        [Dependency] private readonly SolutionContainerSystem _solutionContainerSystem = default!;
        [Dependency] private readonly ItemSlotsSystem _itemSlotsSystem = default!;
        [Dependency] private readonly UserInterfaceSystem _userInterfaceSystem = default!;
        [Dependency] private readonly StorageSystem _storageSystem = default!;
        [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
        [Dependency] private readonly IGamePrototypeLoadManager _gamePrototypeLoadManager = default!;

        private Queue<BiopressComponent> _uiUpdateQueue = new();

        /// <summary>
        ///     A cache of all existant chemical reactions indexed by their resulting reagent.
        /// </summary>
        private IDictionary<string, List<ReactionPrototype>> _reactions = default!;

        public override void Initialize()
        {
            base.Initialize();

            InitializeReactionCache();

            _prototypeManager.PrototypesReloaded += OnPrototypesReloaded;
            _gamePrototypeLoadManager.GamePrototypeLoaded += InitializeReactionCache;

            SubscribeLocalEvent<BiopressComponent, ComponentStartup>((_, comp, _) => UpdateUiState(comp));
            SubscribeLocalEvent<BiopressComponent, SolutionChangedEvent>((_, comp, _) => UpdateUiState(comp));
            SubscribeLocalEvent<BiopressComponent, EntInsertedIntoContainerMessage>((_, comp, _) => UpdateUiState(comp));
            SubscribeLocalEvent<BiopressComponent, EntRemovedFromContainerMessage>((_, comp, _) => UpdateUiState(comp));

            SubscribeLocalEvent<BiopressComponent, PowerChangedEvent>(OnPowerChange);
            SubscribeLocalEvent<BiopressComponent, ContainerIsRemovingAttemptEvent>(OnEntRemoveAttempt);

            SubscribeLocalEvent<BiopressComponent, BoundUIOpenedEvent>((_, comp, _) => UpdateUiState(comp));

            SubscribeLocalEvent<BiopressComponent, BiopressSetModeMessage>(OnSetModeMessage);
            SubscribeLocalEvent<BiopressComponent, BiopressReagentAmountButtonMessage>(OnReagentButtonMessage);

            SubscribeLocalEvent<BiopressComponent, BiopressActivateButtonMessage>(OnActivateButtonMessage);
            SubscribeLocalEvent<BiopressComponent, BiopressStopButtonMessage>(OnStopButtonMessage);

        }

        /// <summary>
        ///     Handles building the reaction cache.
        /// </summary>
        private void InitializeReactionCache()
        {
            _reactions = new Dictionary<string, List<ReactionPrototype>>();

            var reactions = _prototypeManager.EnumeratePrototypes<ReactionPrototype>();
            foreach (var products in reactions)
            {
                CacheReaction(products);
            }
        }

        private void OnPrototypesReloaded(PrototypesReloadedEventArgs eventArgs)
        {
            if (!eventArgs.ByType.TryGetValue(typeof(ReactionPrototype), out var set))
                return;

            foreach (var (reactant, cache) in _reactions)
            {
                cache.RemoveAll((reaction) => set.Modified.ContainsKey(reaction.ID));
                if (cache.Count == 0)
                    _reactions.Remove(reactant);
            }

            foreach (var prototype in set.Modified.Values)
            {
                CacheReaction((ReactionPrototype)prototype);
            }
        }

        private void CacheReaction(ReactionPrototype reaction)
        {
            var reagents = reaction.Products.Keys;
            foreach (var reagent in reagents)
            {
                if (!_reactions.TryGetValue(reagent, out var cache))
                {
                    cache = new List<ReactionPrototype>();
                    _reactions.Add(reagent, cache);
                }

                cache.Add(reaction);
                return; // Only need to cache based on the first reagent.
            }
        }

        private void OnPowerChange(EntityUid uid, BiopressComponent component, ref PowerChangedEvent args)
        {
            EnqueueUiUpdate(component);
        }

        private void OnEntRemoveAttempt(EntityUid uid, BiopressComponent component, ContainerIsRemovingAttemptEvent args)
        {
            if (component.Busy)
                args.Cancel();
        }

        private void EnqueueUiUpdate(BiopressComponent component)
        {
            if (!_uiUpdateQueue.Contains(component)) _uiUpdateQueue.Enqueue(component);
        }

        private void UpdateUiState(BiopressComponent Biopress)
        {
            if (Biopress.Busy)
                return;

            if (!_solutionContainerSystem.TryGetSolution(Biopress.Owner, SharedBiopress.BufferSolutionName, out var bufferSolution))
                return;

            var outputContainer = _itemSlotsSystem.GetItemOrNull(Biopress.Owner, SharedBiopress.OutputSlotName);

            /*if (TryComp(Biopress.Owner, out AppearanceComponent? appearance))
            {
                appearance.SetData(SharedBiopress.BiopressVisualState.OutputAttached, Biopress.OutputSlot.HasItem);
            }*/

            var bufferReagents = bufferSolution.Contents;
            var bufferCurrentVolume = bufferSolution.CurrentVolume;

            var state = new BiopressBoundUserInterfaceState(
                Biopress.Mode, BuildContainerInfo(outputContainer),
                bufferReagents, bufferCurrentVolume);

            _userInterfaceSystem.TrySetUiState(Biopress.Owner, BiopressUiKey.Key, state);
        }

        private void OnSetModeMessage(EntityUid uid, BiopressComponent Biopress, BiopressSetModeMessage message)
        {
            // Ensure the mode is valid, either Transfer or Discard.
            if (!Enum.IsDefined(typeof(BiopressMode), message.BiopressMode))
                return;

            Biopress.Mode = message.BiopressMode;
            UpdateUiState(Biopress);
            ClickSound(Biopress);
        }

        private void OnReagentButtonMessage(EntityUid uid, BiopressComponent Biopress, BiopressReagentAmountButtonMessage message)
        {
            // Ensure the amount corresponds to one of the reagent amount buttons.
            if (!Enum.IsDefined(typeof(BiopressReagentAmount), message.Amount))
                return;

            switch (Biopress.Mode)
            {
                case BiopressMode.Transfer:
                    TransferReagents(Biopress, message.ReagentId, message.Amount.GetFixedPoint(), SharedBiopress.OutputSlotName, message.FromBuffer);
                    break;
                case BiopressMode.Discard:
                    DiscardReagents(Biopress, message.ReagentId, message.Amount.GetFixedPoint(), SharedBiopress.OutputSlotName, message.FromBuffer);
                    break;
                default:
                    // Invalid mode.
                    return;
            }

            ClickSound(Biopress);
        }

        private void OnActivateButtonMessage(EntityUid uid, BiopressComponent component, BiopressActivateButtonMessage message)
        {
            if (!this.IsPowered(component.Owner, EntityManager))
                return;

            ClickSound(component);

            if (!this.IsPowered(component.Owner, EntityManager) ||
                component.Busy)
                return;

            component.Busy = true;

            component.Busy = false;
            UpdateUiState(component);
        }

        private void OnStopButtonMessage(EntityUid uid, BiopressComponent component, BiopressStopButtonMessage message)
        {
            ClickSound(component);

            if (!this.IsPowered(component.Owner, EntityManager) ||
                component.Busy)
                return;

            component.Busy = true;

            component.Busy = false;
            UpdateUiState(component);
        }

        private void TransferReagents(BiopressComponent Biopress, string reagentId, FixedPoint2 amount, string slot, bool fromBuffer)
        {
            var container = _itemSlotsSystem.GetItemOrNull(Biopress.Owner, slot);
            if (container is null ||
                !TryComp<SolutionContainerManagerComponent>(container.Value, out var containerSolution) ||
                !_solutionContainerSystem.TryGetSolution(Biopress.Owner, SharedBiopress.BufferSolutionName, out var bufferSolution))
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

            UpdateUiState(Biopress);
        }

        private void DiscardReagents(BiopressComponent Biopress, string reagentId, FixedPoint2 amount, string slot, bool fromBuffer)
        {

            if (fromBuffer)
            {
                if (_solutionContainerSystem.TryGetSolution(Biopress.Owner, SharedBiopress.BufferSolutionName, out var bufferSolution))
                    bufferSolution.RemoveReagent(reagentId, amount);
                else
                    return;
            }
            else
                return;

            UpdateUiState(Biopress);
        }

        private void ClickSound(BiopressComponent Biopress)
        {
            _audioSystem.Play(Biopress.ClickSound, Filter.Pvs(Biopress.Owner), Biopress.Owner, AudioParams.Default.WithVolume(-2f));
        }

        private BiopressContainerInfo? BuildContainerInfo(EntityUid? container)
        {
            if (container is not { Valid: true })
                return null;

            /*if (!TryComp(container, out FitsInDispenserComponent? fits)
                || !_solutionContainerSystem.TryGetSolution(container.Value, fits.Solution, out var solution))
            {
                return null;
            }*/

            if (TryComp<SolutionContainerManagerComponent>(container, out var solutions))
                foreach (var solution in (solutions.Solutions)) //will only work on the first iter val
                {
                    var reagents = solution.Value.Contents.Select(reagent => (reagent.ReagentId, reagent.Quantity)).ToList();
                    return new BiopressContainerInfo(Name(container.Value), true, solution.Value.CurrentVolume, solution.Value.MaxVolume, reagents);
                }

            return null;
        }

    }
}
