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
        CheckingValves,
        OpeningCrownMaster,
        ClosingCrownOpeningZipper,
        Fracing,
        FinishedFrac,
        PreparingWireline,
        Wirelining
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
        }

        public List<WellState> Wells { get; }
        public Dictionary<Crew, int?> CrewAssignment { get; }
        public Dictionary<Crew, CrewStage> CrewStages { get; }
        public float FracPressure { get; set; }
        public float PumpRate { get; set; }

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

                if (activeFracWells > 0)
                {
                    Pad.FracPressure = 9500 + _random.Next(-50, 50);
                    Pad.PumpRate = 65 + (_random.NextSingle() * 25);
                }
                else
                {
                    Pad.FracPressure = Math.Max(0, Pad.FracPressure - 1800);
                    Pad.PumpRate = Math.Max(0, Pad.PumpRate - 12);
                }

                foreach (WellState well in Pad.Wells)
                {
                    switch (well.Mode)
                    {
                        case OperationMode.Frac:
                            well.WellPressure = Pad.FracPressure;
                            well.PumpDownPressure =0;
                            well.WirelinePressure = 0;
                            break;
                        case OperationMode.Wireline:
                            well.WellPressure = 4800 + _random.Next(-50, 50);
                            well.PumpDownPressure = 0;
                            well.WirelinePressure = well.WirelinePressure;
                            break;
                        default:
                            well.WellPressure = Math.Max(0, well.WellPressure - 500);
                            well.PumpDownPressure = Math.Max(0, well.PumpDownPressure - 300);
                            well.WirelinePressure = Math.Max(0, well.WirelinePressure - 250);
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
                    ScheduleCrew(crew, RandomStageDuration(CrewStage.CheckingValves));
                }
                else
                {
                    WellState well = Pad.Wells[wellIndex.Value];
                    CrewStage nextStage = GetNextStage(Pad.CrewStages[crew], well);
                    ApplyStage(well, crew, nextStage);
                    Pad.CrewStages[crew] = nextStage;
                    status = BuildStageMessage(well, crew, nextStage);

                    if (nextStage == CrewStage.Idle)
                    {
                        Pad.CrewAssignment[crew] = null;
                        well.CurrentCrew = Crew.None;
                    }

                    ScheduleCrew(crew, RandomStageDuration(nextStage));
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
                CrewStage.Idle => CrewStage.CheckingValves,
                CrewStage.CheckingValves => CrewStage.OpeningCrownMaster,
                CrewStage.OpeningCrownMaster => CrewStage.ClosingCrownOpeningZipper,
                CrewStage.ClosingCrownOpeningZipper => CrewStage.Fracing,
                CrewStage.Fracing => CrewStage.FinishedFrac,
                CrewStage.FinishedFrac => CrewStage.PreparingWireline,
                CrewStage.PreparingWireline => CrewStage.Wirelining,
                CrewStage.Wirelining when well.CompletedStages < well.TotalFracStages => CrewStage.CheckingValves,
                CrewStage.Wirelining => CrewStage.Idle,
                _ => CrewStage.Idle
            };
        }

        private void ApplyStage(WellState well, Crew crew, CrewStage stage)
        {
            well.CurrentCrew = crew;
            well.LastUpdatedUtc = DateTime.UtcNow;

            switch (stage)
            {
                case CrewStage.CheckingValves:
                    well.Mode = OperationMode.Standby;
                    SetAllValves(well, ValvePositions.Closed);
                    break;
                case CrewStage.OpeningCrownMaster:
                    well.Valves[ValveNames.Crown] = ValvePositions.Opened;
                    well.Valves[ValveNames.Master] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.Master;
                    break;
                case CrewStage.ClosingCrownOpeningZipper:
                    well.Valves[ValveNames.Crown] = ValvePositions.Closed;
                    well.Valves[ValveNames.Zipper] = ValvePositions.Opened;
                    well.Valves[ValveNames.LowerZipper] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.Zipper;
                    break;
                case CrewStage.Fracing:
                    well.Mode = OperationMode.Frac;
                    break;
                case CrewStage.FinishedFrac:
                    well.CompletedStages++;
                    well.Mode = OperationMode.Standby;
                    well.Valves[ValveNames.LowerZipper] = ValvePositions.Closed;
                    well.Valves[ValveNames.Zipper] = ValvePositions.Closed;
                    well.LastOperatedValve = ValveNames.Zipper;
                    break;
                case CrewStage.PreparingWireline:
                    well.Mode = OperationMode.Wireline;
                    well.Valves[ValveNames.Equalizing] = ValvePositions.Opened;
                    well.Valves[ValveNames.Crown] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.Crown;
                    break;
                case CrewStage.Wirelining:
                    well.Mode = OperationMode.Wireline;
                    well.Valves[ValveNames.OuterPumpDown] = ValvePositions.Opened;
                    well.Valves[ValveNames.InnerPumpDown] = ValvePositions.Opened;
                    well.LastOperatedValve = ValveNames.InnerPumpDown;
                    break;
                case CrewStage.Idle:
                    well.Mode = OperationMode.Standby;
                    SetAllValves(well, ValvePositions.Closed);
                    well.WellPressure = 4800 + _random.Next(50, 150);
                    well.PumpDownPressure = 0;
                    well.WirelinePressure = 0;
                    well.CompletedStages = 0;
                    well.TotalFracStages = _random.Next(3, 7);
                    break;
            }
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
                CrewStage.CheckingValves => TimeSpan.FromSeconds(_random.Next(2, 5)),
                CrewStage.OpeningCrownMaster => TimeSpan.FromSeconds(_random.Next(4, 8)),
                CrewStage.ClosingCrownOpeningZipper => TimeSpan.FromSeconds(_random.Next(4, 8)),
                CrewStage.Fracing => TimeSpan.FromSeconds(_random.Next(25, 35)),
                CrewStage.FinishedFrac => TimeSpan.FromSeconds(_random.Next(2, 4)),
                CrewStage.PreparingWireline => TimeSpan.FromSeconds(_random.Next(3, 6)),
                CrewStage.Wirelining => TimeSpan.FromSeconds(_random.Next(20, 25)),
                _ => TimeSpan.FromSeconds(3)
            };
        }

        private string BuildStageMessage(WellState well, Crew crew, CrewStage stage)
        {
            return stage switch
            {
                CrewStage.OpeningCrownMaster => $"Crew {crew} opened the crown and master valves on {well.Name}.",
                CrewStage.ClosingCrownOpeningZipper => $"Crew {crew} lined up {well.Name} to the zipper manifold.",
                CrewStage.Fracing => $"Crew {crew} is pumping frac stage {well.CompletedStages + 1} of {well.TotalFracStages} on {well.Name}.",
                CrewStage.FinishedFrac when well.CompletedStages < well.TotalFracStages => $"Crew {crew} finished frac stage {well.CompletedStages} of {well.TotalFracStages} on {well.Name}.",
                CrewStage.FinishedFrac => $"Crew {crew} finished the final frac stage on {well.Name}.",
                CrewStage.PreparingWireline => $"Crew {crew} is rigging up wireline after frac stage {well.CompletedStages} on {well.Name}.",
                CrewStage.Wirelining when well.CompletedStages < well.TotalFracStages => $"Crew {crew} completed the wireline run for stage {well.CompletedStages} on {well.Name} and is moving to the next frac stage.",
                CrewStage.Wirelining => $"Crew {crew} is performing the final wireline run on {well.Name}.",
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
