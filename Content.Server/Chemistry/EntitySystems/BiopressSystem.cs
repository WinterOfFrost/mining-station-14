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
using Content.Shared.Audio;
using Robust.Shared.Audio;
using Robust.Shared.Containers;
using Robust.Shared.Player;
using Robust.Shared.Utility;
using Content.Server.Power.Components;
using Content.Server.Power.EntitySystems;
using Robust.Shared.Prototypes;
using Content.Shared.Jittering;

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
        [Dependency] private readonly SharedAudioSystem _sharedAudioSystem = default!;
        [Dependency] private readonly SharedAmbientSoundSystem _ambientSoundSystem = default!;
        [Dependency] private readonly SolutionContainerSystem _solutionContainerSystem = default!;
        [Dependency] private readonly ItemSlotsSystem _itemSlotsSystem = default!;
        [Dependency] private readonly UserInterfaceSystem _userInterfaceSystem = default!;
        [Dependency] private readonly StorageSystem _storageSystem = default!;
        [Dependency] private readonly IPrototypeManager _prototypeManager = default!;
        [Dependency] private readonly IGamePrototypeLoadManager _gamePrototypeLoadManager = default!;
        [Dependency] private readonly SharedJitteringSystem _jitteringSystem = default!;
        [Dependency] private readonly EntityStorageSystem _entityStorageSystem = default!;

        private Queue<BiopressComponent> _uiUpdateQueue = new();
        private Queue<EntityUid> _activeQueue = new();
        private Queue<EntityUid> _checkQueue = new();

        /// <summary>
        ///     A cache of all existant chemical reactions indexed by their resulting reagent.
        /// </summary>
        private IDictionary<string, List<ReactionPrototype>> _reactions = default!;

        public override void Update(float frameTime)
        {

            base.Update(frameTime);

            foreach (var uid in _activeQueue)
            {
                if (!TryComp<BiopressComponent>(uid, out var biopress))
                    continue;

                biopress.ProcessingTimer += frameTime;

                if (biopress.Active && biopress.ProcessingTimer >= biopress.IntervalTime)
                {
                    //check current stage, run appropriate function
                    switch (biopress.Stage)
                    {
                        case BiopressStage.Initial:
                            {
                                HandleSmallMatter(uid, biopress);
                                break;
                            }
                        case BiopressStage.SmallMatter:
                            {
                                var largeMatter = CheckLargeMatter(uid, biopress);

                                if (largeMatter)
                                    HandleLargeMatter(uid, biopress);
                                else
                                    FinalStage(uid, biopress);

                                break;
                            }
                        case BiopressStage.LargeMatter:
                            {
                                var largeMatter = CheckLargeMatter(uid, biopress);

                                if (largeMatter)
                                    HandleLargeMatter(uid, biopress);
                                else
                                    HandleSmallMatter(uid, biopress);

                                break;
                            }
                        case BiopressStage.Final:
                            {
                                biopress.Active = false;
                                break;
                            }
                    }
                }

                _checkQueue.Enqueue(uid);
            }

            _activeQueue.Clear();

            foreach (var uid in _checkQueue)
            {
                if (!TryComp<BiopressComponent>(uid, out var biopress))
                {
                    AfterShutdown(uid);
                    continue;
                }

                if (biopress.Active)
                    _activeQueue.Enqueue(uid);
                else
                    AfterShutdown(uid);
            }

            _checkQueue.Clear();

        }

        public override void Initialize()
        {
            base.Initialize();

            InitializeReactionCache();

            _prototypeManager.PrototypesReloaded += OnPrototypesReloaded;

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

            SubscribeLocalEvent<BiopressComponent, BiopressStoreToggleButtonMessage>(OnStoreToggleButtonMessage);

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

        /// <summary>
        ///     Applies Slash damage to all damageable entities inside hopper, then
        ///     Remove all non-living, non-gibbable entities and generate reagents for buffer
        /// </summary>
        private void HandleSmallMatter(EntityUid uid, BiopressComponent biopress) {
            biopress.ProcessingTimer = 0;
            biopress.Stage = BiopressStage.SmallMatter;
            SoundSystem.Play(biopress.GrindSound.GetSound(), Filter.Pvs(uid), uid, AudioParams.Default);
        }

        /// <summary>
        ///     Applies Blunt damage to all damageable entities inside hopper
        /// </summary>    
        private void HandleLargeMatter(EntityUid uid, BiopressComponent biopress) {
            biopress.ProcessingTimer = 0;
            biopress.Stage = BiopressStage.LargeMatter;
            SoundSystem.Play(biopress.HydraulicSound.GetSound(), Filter.Pvs(uid), uid, AudioParams.Default);
        }

        /// <summary>
        ///     Check for gibbable entities in hopper
        /// </summary> 
        private bool CheckLargeMatter(EntityUid uid, BiopressComponent biopress) {

            return false;
        }

        /// <summary>
        ///     Remove remaining entities, add n ashes to hopper for each entity
        /// </summary> 
        private void FinalStage(EntityUid uid, BiopressComponent biopress) {
            biopress.ProcessingTimer = 0;
            biopress.Stage = BiopressStage.Final;
            SoundSystem.Play(biopress.IncinerateSound.GetSound(), Filter.Pvs(uid), uid, AudioParams.Default);
        }

        private void OnPowerChange(EntityUid uid, BiopressComponent component, ref PowerChangedEvent args)
        {
            EnqueueUiUpdate(component);
            if (!this.IsPowered(component.Owner, EntityManager) && component.Active)
                component.Active = false;
        }

        private void OnEntRemoveAttempt(EntityUid uid, BiopressComponent component, ContainerIsRemovingAttemptEvent args)
        {
            if (component.Active)
                args.Cancel();
        }

        private void EnqueueUiUpdate(BiopressComponent component)
        {
            if (!_uiUpdateQueue.Contains(component)) _uiUpdateQueue.Enqueue(component);
        }

        private void UpdateUiState(BiopressComponent Biopress)
        {
            if (Biopress.Active)
                return;

            if (!_solutionContainerSystem.TryGetSolution(Biopress.Owner, SharedBiopress.BufferSolutionName, out var bufferSolution))
                return;

            var outputContainer = _itemSlotsSystem.GetItemOrNull(Biopress.Owner, SharedBiopress.OutputSlotName);

            /*if (TryComp(Biopress.Owner, out AppearanceComponent? appearance))
            {
                appearance.SetData(SharedBiopress.BiopressVisualState.OutputAttached, Biopress.OutputSlot.HasItem);
            }*/

            var bufferReagents = bufferSolution.Contents;
            var bufferCurrentVolume = bufferSolution.Volume;

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

        private void OnStoreToggleButtonMessage(EntityUid uid, BiopressComponent Biopress, BiopressStoreToggleButtonMessage message)
        {
            if (!TryComp<EntityStorageComponent>(uid, out var storage))
                return;

            if (storage.Open)
                _entityStorageSystem.CloseStorage(uid, storage);
            else {
                _entityStorageSystem.OpenStorage(uid, storage);
                if (storage.IsWeldedShut && storage.Open)
                    storage.IsWeldedShut = false;

                if (storage.Open && Biopress.Active)
                    Biopress.Active = false;
            }
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

        private void AfterShutdown(EntityUid uid)
        {
            RemComp<JitteringComponent>(uid);
            _ambientSoundSystem.SetAmbience(uid, false);
        }

        private void OnActivateButtonMessage(EntityUid uid, BiopressComponent component, BiopressActivateButtonMessage message)
        {

            if (!TryComp<EntityStorageComponent>(uid, out var storage))
                return;

            if (!this.IsPowered(component.Owner, EntityManager) ||
                component.Active ||
                storage.Open)
                return;

            ClickSound(component);
            component.Active = true;
            component.ProcessingTimer = 0;

            _jitteringSystem.AddJitter(uid, -95, 25);
            _sharedAudioSystem.PlayPvs("/Audio/Machines/reclaimer_startup.ogg", uid);
            _ambientSoundSystem.SetAmbience(uid, true);

            _activeQueue.Enqueue(uid);

            component.Stage = BiopressStage.Initial;

            UpdateUiState(component);
        }

        private void OnStopButtonMessage(EntityUid uid, BiopressComponent component, BiopressStopButtonMessage message)
        {
            ClickSound(component);

            if (!this.IsPowered(component.Owner, EntityManager) ||
                !component.Active)
                return;

            component.Active = false;

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
            _audioSystem.Play(Biopress.ClickSound, Filter.Pvs(Biopress.Owner), Biopress.Owner, false, AudioParams.Default.WithVolume(-2f));
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
                    return new BiopressContainerInfo(Name(container.Value), true, solution.Value.Volume, solution.Value.MaxVolume, reagents);
                }

            return null;
        }

    }
}
