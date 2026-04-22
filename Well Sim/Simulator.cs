using System.Timers;

namespace Well_Sim
{
    public enum OperationMode
    {
        Standby,
        Frac,
        Wireline,
    }

    public enum ValvePositions
    {
        Opened,
        Closed
    }

    public enum ValveNames
    {
        Master,
        Zipper,
        LowerZipper,
        Crown,
        InnerPumpDown,
        OuterPumpDown,
        InnerFlowback,
        OuterFlowback,
        Equalizing
    }

    public enum Crew
    {
        None,
        A,
        //B
    }

    public enum CrewStage
    {
        Idle,
        PreparingWireline,
        CheckingValves,
        OpeningInnerPD,
        OpeningOuterPD,
        OpeningCrown,
        RampUpWireline,
        OpeningMasterWireline,
        Wirelining,
        RampDownWireline,
        ClosingMasterWireline,
        RampWireline0,
        ClosingCrown,
        ClosingOuterPD,
        ClosingInnerPD,
        PreparingFrac,
        OpeningZipper,
        OpeningLowerZipper,
        RampUpFrac,
        OpeningMasterFrac,
        Fracing,
        RampDownFrac,
        ClosingMasterFrac,
        RampFrac0,
        ClosingLowerZipper,
        ClosingZipper,
        FinishedFrac,
    }

    public enum PadColor
    {
        Red = 1,
        White = 2,
        Blue = 4,
        Tan = 8,
        DarkGrey = 16,
        Green = 32,
        Orange = 64,
        Purple = 128,
        Brown = 256,
        Black = 512,
        Yellow = 1024,
        Cyan = 2048
    }

    internal sealed class WellState
    {
        public WellState(string name, PadColor color)
        {
            Name = name;
            Color = color;
            Mode = OperationMode.Standby;
            Valves = Enum
                .GetValues<ValveNames>()
                .ToDictionary(valve => valve, _ => ValvePositions.Closed);
            LastUpdatedUtc = DateTime.UtcNow;
            WellPressureRamp = PressureRampState.Inactive;
        }

        public string Name { get; }
        public PadColor Color { get; }
        public OperationMode Mode { get; set; }
        public Dictionary<ValveNames, ValvePositions> Valves { get; }
        public float WellPressure { get; set; }
        public float PumpDownPressure { get; set; }
        public float WirelinePressure { get; set; }
        public Crew CurrentCrew { get; set; }
        public ValveNames? LastOperatedValve { get; set; }
        public int CompletedStages { get; set; }
        public int TotalFracStages { get; set; }
        public bool JobComplete { get; set; }
        public DateTime LastUpdatedUtc { get; set; }

        public PressureRampState WellPressureRamp { get; set; }
    }

    internal readonly record struct PressureRampState(
        bool IsActive,
        float StartValue,
        float TargetValue,
        DateTime StartUtc,
        TimeSpan Duration)
    {
        public static PressureRampState Inactive => new(false, 0, 0, default, default);
    }

    internal sealed class PadState
    {
        public PadState()
        {
            Wells = BuildDefaultWells();
            CrewAssignment = Enum
                .GetValues<Crew>()
                .Where(crew => crew != Crew.None)
                .ToDictionary(crew => crew, _ => (int?)null);
            CrewStages = Enum
                .GetValues<Crew>()
                .Where(crew => crew != Crew.None)
                .ToDictionary(crew => crew, _ => CrewStage.Idle);

            FracPressure = 0.0f;
            PumpRate = 0.0f;
            FracPressureRamp = PressureRampState.Inactive;
        }

        public List<WellState> Wells { get; }
        public Dictionary<Crew, int?> CrewAssignment { get; }
        public Dictionary<Crew, CrewStage> CrewStages { get; }
        public float FracPressure { get; set; }
        public float PumpRate { get; set; }
        public PressureRampState FracPressureRamp { get; set; }

        private static List<WellState> BuildDefaultWells()
        {
            var colorPool = Enum.GetValues<PadColor>().ToList();
            var random = new Random();
            var wells = new List<WellState>(12);

            for (int i = 0; i < 12; i++)
            {
                if (colorPool.Count == 0)
                    break;

                int index = random.Next(colorPool.Count);
                var color = colorPool[index];
                colorPool.RemoveAt(index);
                char letter = (char)random.Next('A', 'Z' + 1);
                int number = random.Next(1, 100);
                string name = $"{color} {letter}-{number:D2}";

                wells.Add(new WellState(name, color));
            }

            return wells;
        }
    }

    internal sealed class SimulationSnapshot
    {
        public string Summary { get; init; } = string.Empty;
        public float FracPressure { get; init; }
        public float PumpRate { get; init; }
        public IReadOnlyList<WellSnapshot> Wells { get; init; } = Array.Empty<WellSnapshot>();
    }

    internal sealed class WellSnapshot
    {
        public string Name { get; init; } = string.Empty;
        public PadColor Color { get; init; }
        public OperationMode Mode { get; init; }
        public IReadOnlyDictionary<ValveNames, ValvePositions> Valves { get; init; } = new Dictionary<ValveNames, ValvePositions>();
        public float WellPressure { get; init; }
        public float PumpDownPressure { get; init; }
        public float WirelinePressure { get; init; }
        public Crew CurrentCrew { get; init; }
        public int CompletedStages { get; init; }
        public int TotalFracStages { get; init; }
        public bool JobComplete { get; init; }
    }

    internal sealed class Simulator : IDisposable
    {
        private readonly Random _random = new();
        private readonly Lock _sync = new();
        private readonly Dictionary<Crew, System.Timers.Timer> _crewTimers = new();
        private readonly System.Timers.Timer _padTimer;
        private bool _disposed;

        public Simulator()
        {
            Pad = new PadState();
            _padTimer = new System.Timers.Timer(1000);
            _padTimer.AutoReset = true;
            _padTimer.Elapsed += PadTimerElapsed;

            foreach (Crew crew in Enum.GetValues<Crew>().Where(crew => crew != Crew.None))
            {
                var timer = new System.Timers.Timer();
                timer.AutoReset = false;
                timer.Elapsed += (_, _) => AdvanceCrew(crew);
                _crewTimers[crew] = timer;
            }
        }

        public event Action<string>? StatusChanged;
        public event Action<SimulationSnapshot>? SnapshotChanged;

        public PadState Pad { get; }

        public bool IsRunning { get; private set; }

        public void Start()
        {
            lock (_sync)
            {
                if (IsRunning)
                {
                    PublishStatus("Simulation already running.");
                    return;
                }

                ResetPad();
                IsRunning = true;
                _padTimer.Start();

                foreach (Crew crew in _crewTimers.Keys)
                {
                    ScheduleCrew(crew, TimeSpan.FromMilliseconds(_random.Next(500, 1500)));
                }
            }

            PublishStatus("Frac pad simulation started.");
            PublishSnapshot();
        }

        public void Stop()
        {
            lock (_sync)
            {
                if (!IsRunning)
                {
                    return;
                }

                IsRunning = false;
                _padTimer.Stop();

                foreach (System.Timers.Timer timer in _crewTimers.Values)
                {
                    timer.Stop();
                }

                foreach (WellState well in Pad.Wells)
                {
                    well.Mode = OperationMode.Standby;
                    well.CurrentCrew = Crew.None;
                    well.WellPressure = 0;
                    well.PumpDownPressure = 0;
                    well.WirelinePressure = 0;
                }

                foreach (Crew crew in Pad.CrewAssignment.Keys.ToList())
                {
                    Pad.CrewAssignment[crew] = null;
                    Pad.CrewStages[crew] = CrewStage.Idle;
                }

                Pad.FracPressure = 0;
                Pad.PumpRate = 0;
            }

            PublishStatus("Simulation stopped.");
            PublishSnapshot();
        }

        private void ResetPad()
        {
            foreach (WellState well in Pad.Wells)
            {
                foreach (ValveNames valve in well.Valves.Keys.ToList())
                {
                    well.Valves[valve] = ValvePositions.Closed;
                }

                well.Mode = OperationMode.Standby;
                well.CurrentCrew = Crew.None;
                well.LastOperatedValve = null;
                well.WellPressure = 0;
                well.PumpDownPressure = 0;
                well.WirelinePressure = 0;
                well.CompletedStages = 0;
                well.TotalFracStages = _random.Next(3, 7);
                well.JobComplete = false;
                well.LastUpdatedUtc = DateTime.UtcNow;
            }

            foreach (Crew crew in Pad.CrewAssignment.Keys.ToList())
            {
                Pad.CrewAssignment[crew] = null;
                Pad.CrewStages[crew] = CrewStage.Idle;
            }

            Pad.FracPressure = 0;
            Pad.PumpRate = 0;
            Pad.FracPressureRamp = PressureRampState.Inactive;
        }

        private void PadTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            lock (_sync)
            {
                if (!IsRunning)
                {
                    return;
                }

                int activeFracWells = Pad.Wells.Count(well => well.Mode == OperationMode.Frac);
                int activeWirelineWells = Pad.Wells.Count(well => well.Mode == OperationMode.Wireline);
                bool zipperLinedUp = Pad.Wells.Any(well =>
                    well.Valves[ValveNames.LowerZipper] == ValvePositions.Opened &&
                    well.Valves[ValveNames.Zipper] == ValvePositions.Opened);

                // "Frac pressure" should not instantly jump just because a well is lined up to the zipper.
                // Only actual frac mode or an explicit ramp should drive Pad.FracPressure upward.
                bool fracHydraulicsActive = activeFracWells > 0 || Pad.FracPressureRamp.IsActive;

                if (fracHydraulicsActive)
                {
                    if (!TryApplyPadFracPressureRamp(Pad))
                    {
                        // Keep frac pressure roughly near the intended max once active (during Frac mode).
                        Pad.FracPressure = Math.Max(Pad.FracPressure + _random.Next(-50, 50), 10_300);
                        Pad.PumpRate = 65 + (_random.NextSingle() * 25);
                    }
                }
                else if (zipperLinedUp && Pad.FracPressure > 0.01f)
                {
                    // While lined up to the frac manifold (but before Frac mode starts), hold pressure steady.
                    // This prevents a mid-sequence drop (e.g., during OpeningMasterFrac) without reintroducing
                    // an artificial jump to max.
                    Pad.FracPressure = Math.Max(0, Pad.FracPressure + _random.Next(-10, 10));
                    Pad.PumpRate = Math.Max(0, Pad.PumpRate - 4);
                    Pad.FracPressureRamp = PressureRampState.Inactive;
                }
                else
                {
                    Pad.FracPressure = Math.Max(0, Pad.FracPressure - 1800);
                    Pad.PumpRate = Math.Max(0, Pad.PumpRate - 12);
                    Pad.FracPressureRamp = PressureRampState.Inactive;
                }

                foreach (WellState well in Pad.Wells)
                {
                    if (well.Valves[ValveNames.InnerPumpDown] == ValvePositions.Opened && well.Valves[ValveNames.OuterPumpDown] == ValvePositions.Opened)
                    {
                        well.PumpDownPressure = well.WellPressure + _random.Next(-5, 5);
                        well.WirelinePressure = well.WellPressure + _random.Next(-5, 5);
                    }

                    if (TryApplyWellPressureRamp(well))
                    {
                        well.LastUpdatedUtc = DateTime.UtcNow;
                        continue;
                    }

                    switch (well.Mode)
                    {
                        case OperationMode.Frac:
                            well.WellPressure = Pad.FracPressure + _random.Next(-5, 5);
                            //well.PumpDownPressure = 0;
                            //well.WirelinePressure = 0;
                            break;
                        case OperationMode.Wireline:
                            well.WellPressure = 6_200 + _random.Next(-50, 50);
                            //well.PumpDownPressure = well.WellPressure;
                            //well.WirelinePressure = well.WellPressure;
                            break;
                        default:
                            if (well.Valves[ValveNames.LowerZipper] == ValvePositions.Opened &&
                                well.Valves[ValveNames.Zipper] == ValvePositions.Opened)
                            {
                                well.WellPressure = Pad.FracPressure + _random.Next(-5, 5);
                            }
                            well.WellPressure = well.WellPressure;
                            well.PumpDownPressure = well.PumpDownPressure;
                            well.WirelinePressure = well.WirelinePressure;
                            //well.WellPressure = Math.Max(0, well.WellPressure - 500);
                            //well.PumpDownPressure = Math.Max(0, well.PumpDownPressure - 300);
                            //well.WirelinePressure = Math.Max(0, well.WirelinePressure - 250);
                            break;
                    }

                    well.LastUpdatedUtc = DateTime.UtcNow;
                }

            }

            PublishSnapshot();
        }

        private void AdvanceCrew(Crew crew)
        {
            string? status = null;

            lock (_sync)
            {
                if (!IsRunning)
                {
                    return;
                }

                int? wellIndex = Pad.CrewAssignment[crew];
                if (wellIndex is null)
                {
                    int? newWellIndex = FindNextAvailableWell();
                    if (newWellIndex is null)
                    {
                        ScheduleCrew(crew, TimeSpan.FromSeconds(_random.Next(3, 6)));
                        return;
                    }

                    wellIndex = newWellIndex;
                    Pad.CrewAssignment[crew] = wellIndex;
                    WellState assignedWell = Pad.Wells[wellIndex.Value];
                    assignedWell.CurrentCrew = crew;
                    Pad.CrewStages[crew] = CrewStage.CheckingValves;
                    status = $"Crew {crew} assigned to {assignedWell.Name} and is checking valves.";
                    TimeSpan stageDuration = RandomStageDuration(CrewStage.CheckingValves);
                    ApplyStage(assignedWell, crew, CrewStage.CheckingValves, stageDuration);
                    ScheduleCrew(crew, stageDuration);
                }
                else
                {
                    WellState well = Pad.Wells[wellIndex.Value];
                    CrewStage nextStage = GetNextStage(Pad.CrewStages[crew], well);
                    TimeSpan stageDuration = RandomStageDuration(nextStage);
                    ApplyStage(well, crew, nextStage, stageDuration);
                    Pad.CrewStages[crew] = nextStage;
                    status = BuildStageMessage(well, crew, nextStage);

                    if (nextStage == CrewStage.Idle)
                    {
                        Pad.CrewAssignment[crew] = null;
                        well.CurrentCrew = Crew.None;
                    }

                    ScheduleCrew(crew, stageDuration);
                }
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                PublishStatus(status);
            }

            PublishSnapshot();
        }

        private int? FindNextAvailableWell()
        {
            List<int> candidates = Pad.Wells
                .Select((well, index) => new { well, index })
                .Where(item => item.well.CurrentCrew == Crew.None)
                .Select(item => item.index)
                .ToList();

            if (candidates.Count == 0)
            {
                return null;
            }

            return candidates[_random.Next(candidates.Count)];
        }

        private static CrewStage GetNextStage(CrewStage currentStage, WellState well)
        {
            return currentStage switch
            {
                CrewStage.Idle => CrewStage.PreparingWireline,
                CrewStage.PreparingWireline => CrewStage.CheckingValves,
                CrewStage.CheckingValves => CrewStage.OpeningInnerPD,
                CrewStage.OpeningInnerPD => CrewStage.OpeningOuterPD,
                CrewStage.OpeningOuterPD => CrewStage.OpeningCrown,
                CrewStage.OpeningCrown => CrewStage.RampUpWireline,
                CrewStage.RampUpWireline =>CrewStage.OpeningMasterWireline,
                CrewStage.OpeningMasterWireline => CrewStage.Wirelining,
                CrewStage.Wirelining => CrewStage.RampDownWireline,
                CrewStage.RampDownWireline => CrewStage.ClosingMasterWireline,
                CrewStage.ClosingMasterWireline => CrewStage.RampWireline0,
                CrewStage.RampWireline0 => CrewStage.ClosingCrown,
                CrewStage.ClosingCrown => CrewStage.ClosingOuterPD,
                CrewStage.ClosingOuterPD => CrewStage.ClosingInnerPD,
                CrewStage.ClosingInnerPD => CrewStage.PreparingFrac,
                CrewStage.PreparingFrac => CrewStage.OpeningZipper,
                CrewStage.OpeningZipper => CrewStage.OpeningLowerZipper,
                CrewStage.OpeningLowerZipper => CrewStage.RampUpFrac,
                CrewStage.RampUpFrac => CrewStage.OpeningMasterFrac,
                CrewStage.OpeningMasterFrac => CrewStage.Fracing,
                CrewStage.Fracing => CrewStage.RampDownFrac,
                CrewStage.RampDownFrac => CrewStage.ClosingMasterFrac,
                CrewStage.ClosingMasterFrac => CrewStage.RampFrac0,
                CrewStage.RampFrac0 => CrewStage.ClosingLowerZipper,
                CrewStage.ClosingLowerZipper => CrewStage.ClosingZipper,
                CrewStage.ClosingZipper => CrewStage.FinishedFrac,
                CrewStage.FinishedFrac when well.CompletedStages < well.TotalFracStages => CrewStage.PreparingWireline,
                CrewStage.FinishedFrac => CrewStage.Idle,
                _ => CrewStage.Idle
            };
        }

        private void ApplyStage(WellState well, Crew crew, CrewStage stage, TimeSpan stageDuration)
        {
            well.CurrentCrew = crew;
            well.LastUpdatedUtc = DateTime.UtcNow;

            switch (stage)
            {
                case CrewStage.PreparingWireline:
                    well.Mode = OperationMode.Standby;
                    well.WellPressure = 0;
                    well.PumpDownPressure = 0;
                    well.WirelinePressure = 0;
                    SetAllValves(well, ValvePositions.Closed);
                    well.WellPressureRamp = PressureRampState.Inactive;
                    break;
                case CrewStage.CheckingValves:
                    well.Mode = OperationMode.Standby;
                    well.WellPressure = 0;
                    well.PumpDownPressure = 0;
                    well.WirelinePressure = 0;
                    SetAllValves(well, ValvePositions.Closed);
                    well.WellPressureRamp = PressureRampState.Inactive;
                    break;
                case CrewStage.OpeningInnerPD:
                    well.Valves[ValveNames.InnerPumpDown] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.InnerPumpDown;
                    break;
                case CrewStage.OpeningOuterPD:
                    // Well and PD Pressure linked
                    well.Valves[ValveNames.OuterPumpDown] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.OuterPumpDown;
                    break;
                case CrewStage.OpeningCrown:
                    well.Valves[ValveNames.Crown] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.Crown;
                    break;
                case CrewStage.RampUpWireline:
                    well.Mode = OperationMode.Wireline;
                    BeginWellPressureRamp(well, 4_200 + _random.Next(50, 150), stageDuration);
                    break;
                case CrewStage.OpeningMasterWireline:
                    well.Valves[ValveNames.Master] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.Master;
                    break;
                case CrewStage.Wirelining:
                    BeginWellPressureRamp(well, 6_200 + _random.Next(50, 150), TimeSpan.FromSeconds(4));
                    break;
                case CrewStage.RampDownWireline:
                    BeginWellPressureRamp(well, 4_200 + _random.Next(50, 150), stageDuration);
                    break;
                case CrewStage.ClosingMasterWireline:
                    // Prevent `PadTimerElapsed` from snapping pressure back to the "wireline steady-state" (6,200)
                    // between the end of RampDownWireline and the start of RampWireline0.
                    well.Mode = OperationMode.Standby;
                    well.Valves[ValveNames.Master] = ValvePositions.Closed;
                    well.LastOperatedValve = ValveNames.Master;
                    break;
                case CrewStage.RampWireline0:
                    BeginWellPressureRamp(well, 0, stageDuration);
                    break;
                case CrewStage.ClosingCrown:
                    well.WellPressureRamp = PressureRampState.Inactive;
                    well.WellPressure = 0;
                    well.PumpDownPressure = 0;
                    well.WirelinePressure = 0;
                    well.Valves[ValveNames.Crown] = ValvePositions.Closed;
                    well.LastOperatedValve = ValveNames.Crown;
                    break;
                case CrewStage.ClosingOuterPD:
                    well.Valves[ValveNames.OuterPumpDown] = ValvePositions.Closed;
                    well.LastOperatedValve = ValveNames.OuterPumpDown;
                    break;
                case CrewStage.ClosingInnerPD:
                    well.Valves[ValveNames.InnerPumpDown] = ValvePositions.Closed;
                    well.LastOperatedValve = ValveNames.InnerPumpDown;
                    break;

                case CrewStage.PreparingFrac:
                    well.Mode = OperationMode.Standby;
                    well.WellPressure = 0;
                    Pad.FracPressure = 0;
                    break;
                case CrewStage.OpeningZipper:
                    well.Valves[ValveNames.Zipper] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.Zipper;
                    break;
                case CrewStage.OpeningLowerZipper:
                    well.Valves[ValveNames.LowerZipper] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.LowerZipper;
                    break;
                case CrewStage.RampUpFrac:
                    well.WellPressureRamp = PressureRampState.Inactive;
                    Pad.FracPressure = 0;
                    BeginPadFracPressureRamp(Pad, 4_200, stageDuration);
                    break;
                case CrewStage.OpeningMasterFrac:
                    well.Valves[ValveNames.Master] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.Master;
                    break;
                case CrewStage.Fracing:
                    BeginPadFracPressureRamp(Pad, 10_300 + _random.Next(50, 150), TimeSpan.FromSeconds(6));
                    well.Mode = OperationMode.Frac;
                    well.WellPressureRamp = PressureRampState.Inactive;
                    break;
                case CrewStage.RampDownFrac:
                    well.WellPressureRamp = PressureRampState.Inactive;
                    // Stop treating this well as actively fracing so PadTimerElapsed won't clamp Pad.FracPressure
                    // back up to the max once the ramp completes.
                    well.Mode = OperationMode.Standby;
                    BeginPadFracPressureRamp(Pad, 4_200 + _random.Next(50, 150), stageDuration);
                    break;
                case CrewStage.ClosingMasterFrac:
                    well.Valves[ValveNames.Master] = ValvePositions.Closed;
                    well.LastOperatedValve = ValveNames.Master;
                    break;
                case CrewStage.RampFrac0:
                    well.WellPressureRamp = PressureRampState.Inactive;
                    BeginPadFracPressureRamp(Pad, 0, stageDuration);
                    break;
                case CrewStage.ClosingLowerZipper:
                    well.Mode = OperationMode.Standby;
                    well.WellPressure = 0;
                    well.Valves[ValveNames.LowerZipper] = ValvePositions.Closed;
                    well.LastOperatedValve = ValveNames.LowerZipper;
                    break;
                case CrewStage.ClosingZipper:
                    well.WellPressureRamp = PressureRampState.Inactive;
                    well.Valves[ValveNames.Zipper] = ValvePositions.Closed;
                    well.LastOperatedValve = ValveNames.Zipper;
                    break;
                case CrewStage.FinishedFrac:
                    well.CompletedStages++;
                    break;
                case CrewStage.Idle:
                    well.Mode = OperationMode.Standby;
                    SetAllValves(well, ValvePositions.Closed);
                    well.PumpDownPressure = 0;
                    well.WirelinePressure = 0;
                    well.WellPressureRamp = PressureRampState.Inactive;
                    well.CompletedStages = 0;
                    well.TotalFracStages = _random.Next(3, 7);
                    break;
            }
        }

        /// <summary>
        /// Use inside an `ApplyStage` case to ramp `WellPressure` from its current value
        /// to `targetPressure` over the current crew stage duration.
        /// </summary>
        private static void BeginWellPressureRamp(WellState well, float targetPressure, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                well.WellPressure = targetPressure;
                well.WellPressureRamp = PressureRampState.Inactive;
                return;
            }

            well.WellPressureRamp = new PressureRampState(
                IsActive: true,
                StartValue: well.WellPressure,
                TargetValue: targetPressure,
                StartUtc: DateTime.UtcNow,
                Duration: duration);
        }

        private static bool TryApplyWellPressureRamp(WellState well)
        {
            PressureRampState ramp = well.WellPressureRamp;
            if (!ramp.IsActive)
            {
                return false;
            }

            TimeSpan elapsed = DateTime.UtcNow - ramp.StartUtc;
            if (elapsed >= ramp.Duration)
            {
                well.WellPressure = ramp.TargetValue;
                well.WellPressureRamp = PressureRampState.Inactive;
                return true;
            }

            double t = elapsed.TotalMilliseconds / ramp.Duration.TotalMilliseconds;
            t = Math.Clamp(t, 0.0, 1.0);
            well.WellPressure = (float)(ramp.StartValue + ((ramp.TargetValue - ramp.StartValue) * t));
            return true;
        }

        private static void BeginPadFracPressureRamp(PadState pad, float targetPressure, TimeSpan duration)
        {
            if (duration <= TimeSpan.Zero)
            {
                pad.FracPressure = targetPressure;
                pad.FracPressureRamp = PressureRampState.Inactive;
                return;
            }

            pad.FracPressureRamp = new PressureRampState(
                IsActive: true,
                StartValue: pad.FracPressure,
                TargetValue: targetPressure,
                StartUtc: DateTime.UtcNow,
                Duration: duration);
        }

        private static bool TryApplyPadFracPressureRamp(PadState pad)
        {
            PressureRampState ramp = pad.FracPressureRamp;
            if (!ramp.IsActive)
            {
                return false;
            }

            TimeSpan elapsed = DateTime.UtcNow - ramp.StartUtc;
            if (elapsed >= ramp.Duration)
            {
                pad.FracPressure = ramp.TargetValue;
                pad.FracPressureRamp = PressureRampState.Inactive;
                return true;
            }

            double t = elapsed.TotalMilliseconds / ramp.Duration.TotalMilliseconds;
            t = Math.Clamp(t, 0.0, 1.0);
            pad.FracPressure = (float)(ramp.StartValue + ((ramp.TargetValue - ramp.StartValue) * t));
            return true;
        }

        private static void SetAllValves(WellState well, ValvePositions position)
        {
            foreach (ValveNames valve in well.Valves.Keys.ToList())
            {
                well.Valves[valve] = position;
            }
        }

        private TimeSpan RandomStageDuration(CrewStage stage)
        {
            return stage switch
            {
                CrewStage.Idle => TimeSpan.FromSeconds(_random.Next(2, 4)),
                CrewStage.PreparingWireline => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.CheckingValves => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.OpeningInnerPD => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.OpeningOuterPD => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.OpeningCrown => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.RampUpWireline => TimeSpan.FromSeconds(_random.Next(5, 6)),
                CrewStage.OpeningMasterWireline => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.Wirelining => TimeSpan.FromSeconds(_random.Next(12, 18)),
                CrewStage.RampDownWireline => TimeSpan.FromSeconds(_random.Next(5, 6)),
                CrewStage.ClosingMasterWireline => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.RampWireline0 => TimeSpan.FromSeconds(_random.Next(3, 4)),
                CrewStage.ClosingCrown => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.ClosingOuterPD => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.ClosingInnerPD => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.PreparingFrac => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.OpeningZipper => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.OpeningLowerZipper => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.RampUpFrac => TimeSpan.FromSeconds(_random.Next(5, 7)),
                CrewStage.OpeningMasterFrac => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.Fracing => TimeSpan.FromSeconds(_random.Next(15, 20)),
                CrewStage.RampDownFrac => TimeSpan.FromSeconds(_random.Next(5, 7)),
                CrewStage.ClosingMasterFrac => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.RampFrac0 => TimeSpan.FromSeconds(_random.Next(3, 4)),
                CrewStage.ClosingLowerZipper => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.ClosingZipper => TimeSpan.FromSeconds(_random.Next(2, 3)),
                CrewStage.FinishedFrac => TimeSpan.FromSeconds(_random.Next(2, 4)),
                _ => TimeSpan.FromSeconds(3)
            };
        }

        private string BuildStageMessage(WellState well, Crew crew, CrewStage stage)
        {
            return stage switch
            {
                CrewStage.PreparingWireline => $"Crew {crew} is rigging up wireline after frac stage {well.CompletedStages} on {well.Name}.",
                CrewStage.OpeningInnerPD => $"Crew {crew} opened the inner pump down valve on {well.Name}.",
                CrewStage.OpeningOuterPD => $"Crew {crew} opened the outer pump down valve on {well.Name}.",
                CrewStage.OpeningCrown => $"Crew {crew} opened the crown valve on {well.Name}.",
                CrewStage.RampUpWireline => $"Crew {crew} is ramping up wireline pressure on {well.Name}.",
                CrewStage.OpeningMasterWireline => $"Crew {crew} opened the master valve for wireline on {well.Name}.",
                CrewStage.Wirelining when well.CompletedStages < well.TotalFracStages => $"Crew {crew} completed the wireline run for stage {well.CompletedStages} on {well.Name} and is moving to the next frac stage.",
                CrewStage.Wirelining => $"Crew {crew} is performing the final wireline run on {well.Name}.",
                CrewStage.RampDownWireline => $"Crew {crew} is ramping down wireline pressure on {well.Name}.",
                CrewStage.ClosingMasterWireline => $"Crew {crew} closed the master valve for wireline on {well.Name}.",
                CrewStage.RampWireline0 => $"Crew {crew} is bleeding off remaining wireline pressure on {well.Name}.",
                CrewStage.ClosingCrown => $"Crew {crew} closed the crown valve on {well.Name}.",
                CrewStage.ClosingOuterPD => $"Crew {crew} closed the outer pump down valve on {well.Name}.",
                CrewStage.ClosingInnerPD => $"Crew {crew} closed the inner pump down valve on {well.Name}.",
                CrewStage.PreparingFrac => $"Crew {crew} is preparing for the next frac stage on {well.Name}.",
                CrewStage.OpeningZipper => $"Crew {crew} opened the zipper valve on {well.Name}.",
                CrewStage.OpeningLowerZipper => $"Crew {crew} opened the lower zipper valve on {well.Name}.",
                CrewStage.RampUpFrac => $"Crew {crew} is ramping up frac pressure on {well.Name}.",
                CrewStage.OpeningMasterFrac => $"Crew {crew} opened the master valve for frac on {well.Name}.",
                CrewStage.Fracing => $"Crew {crew} is pumping frac stage {well.CompletedStages + 1} of {well.TotalFracStages} on {well.Name}.",
                CrewStage.RampDownFrac => $"Crew {crew} is ramping down frac pressure on {well.Name}.",
                CrewStage.ClosingMasterFrac => $"Crew {crew} closed the master valve for frac on {well.Name}.",
                CrewStage.RampFrac0 => $"Crew {crew} is bleeding off remaining frac pressure on {well.Name}.",
                CrewStage.ClosingLowerZipper => $"Crew {crew} closed the lower zipper valve on {well.Name}.",
                CrewStage.ClosingZipper => $"Crew {crew} closed the zipper valve on {well.Name}.",
                CrewStage.FinishedFrac when well.CompletedStages < well.TotalFracStages => $"Crew {crew} finished frac stage {well.CompletedStages} of {well.TotalFracStages} on {well.Name}.",
                CrewStage.FinishedFrac => $"Crew {crew} finished the final frac stage on {well.Name}.",
                CrewStage.Idle => $"Crew {crew} wrapped up {well.Name} and is ready for the next assignment.",
                _ => $"Crew {crew} is working {stage} on {well.Name}."
            };
        }

        private void ScheduleCrew(Crew crew, TimeSpan dueIn)
        {
            System.Timers.Timer timer = _crewTimers[crew];
            timer.Stop();
            timer.Interval = Math.Max(250, dueIn.TotalMilliseconds);
            timer.Start();
        }

        private void PublishStatus(string message)
        {
            StatusChanged?.Invoke(message);
        }

        private void PublishSnapshot()
        {
            string summary;
            List<WellSnapshot> wells;

            lock (_sync)
            {
                summary = $"Active frac wells: {Pad.Wells.Count(well => well.Mode == OperationMode.Frac)}, " +
                          $"wireline wells: {Pad.Wells.Count(well => well.Mode == OperationMode.Wireline)}, " +
                          $"completed wells: {Pad.Wells.Count(well => well.JobComplete)}.";
                wells = Pad.Wells
                    .Select(well => new WellSnapshot
                    {
                        Name = well.Name,
                        Color = well.Color,
                        Mode = well.Mode,
                        Valves = new Dictionary<ValveNames, ValvePositions>(well.Valves),
                        WellPressure = well.WellPressure,
                        PumpDownPressure = well.PumpDownPressure,
                        WirelinePressure = well.WirelinePressure,
                        CurrentCrew = well.CurrentCrew,
                        CompletedStages = well.CompletedStages,
                        TotalFracStages = well.TotalFracStages,
                        JobComplete = well.JobComplete
                    })
                    .ToList();
            }

            SnapshotChanged?.Invoke(new SimulationSnapshot
            {
                Summary = summary,
                FracPressure = Pad.FracPressure,
                PumpRate = Pad.PumpRate,
                Wells = wells
            });
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Stop();
            _padTimer.Dispose();

            foreach (System.Timers.Timer timer in _crewTimers.Values)
            {
                timer.Dispose();
            }

            _disposed = true;
        }
    }
}
