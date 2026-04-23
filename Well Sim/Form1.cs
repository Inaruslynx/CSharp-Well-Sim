using Opc.Ua;
using Opc.Ua.Client;
using Opc.Ua.Configuration;
using System.Collections.Concurrent;
using System.Diagnostics;
using static System.Collections.Specialized.BitVector32;

namespace Well_Sim
{
    public partial class frmWellSim : Form
    {
        private const ushort IgnitionTagNamespaceIndex = 2;
        private const string IgnitionTagProvider = "Demo";
        private const string IgnitionPadFolder = "Pad";

        private Opc.Ua.ApplicationConfiguration? _configuration;
        private Session? _session;
        private readonly Simulator _simulator;
        private readonly SemaphoreSlim _tagWriteGate = new(1, 1);
        private readonly ConcurrentQueue<SimulationSnapshot> _pendingSnapshots = new();
        private volatile SimulationSnapshot? _latestSnapshot;
        private CancellationTokenSource? _cts;
        private Task? _writeLoopTask;
        private readonly Dictionary<string, int> _userPinsByRole = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, (bool ZipperOpened, bool LowerZipperOpened)> _lastValveStatesByWell = new();
        private bool _sentInitialFullSnapshot;

        public frmWellSim()
        {
            InitializeComponent();
            _simulator = new Simulator();
            _simulator.StatusChanged += Simulator_StatusChanged;
            _simulator.SnapshotChanged += Simulator_SnapshotChanged;
            FormClosing += frmWellSim_FormClosing;
        }

        private async void btnStart_Click(object sender, EventArgs e)
        {
            if (_session?.Connected == true)
            {
                StartSimulation();
                return;
            }

            if (!int.TryParse(txtPort.Text, out int port) || port <= 0 || port > 65535)
            {
                MessageBox.Show("Enter a valid OPC UA port number.", "Invalid Port", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtPort.Focus();
                txtPort.SelectAll();
                return;
            }

            string host = txtAddress.Text.Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                MessageBox.Show("Enter an OPC UA server address.", "Missing Address", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtAddress.Focus();
                return;
            }

            if (!int.TryParse(txtRate.Text, out int rate) || rate <= 0)
            {
                MessageBox.Show("Enter a valid update rate in milliseconds.", "Invalid Update Rate", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                txtRate.Focus();
                txtRate.SelectAll();
                return;
            }

            ToggleControls(isConnecting: true);
            SetStatus("Connecting...");

            try
            {
                _configuration ??= await CreateConfigurationAsync();

                string endpointUrl = $"opc.tcp://{host}:{port}";
                EndpointDescription selectedEndpoint = await SelectEndpointAsync(endpointUrl);
                var endpointConfiguration = EndpointConfiguration.Create(_configuration);
                var configuredEndpoint = new ConfiguredEndpoint(null, selectedEndpoint, endpointConfiguration);
                IUserIdentity? userIdentity = CreateUserIdentity();

                _session = await Session.Create(
                    _configuration,
                    configuredEndpoint,
                    false,
                    "Well Simulator",
                    60_000,
                    userIdentity,
                    null,
                    CancellationToken.None);

                await LoadUserPinsAsync(_session);
                _sentInitialFullSnapshot = false;
                StartSimulation();
                _writeLoopTask = StartWriteLoopAsync(_session, TimeSpan.FromMilliseconds(rate));
            }
            catch (Exception ex)
            {
                await DisconnectAsync();
                SetStatus("Connection failed");
                MessageBox.Show($"Could not connect to the OPC UA server.\r\n\r\n{ex.Message}", "Connection Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                ToggleControls(isConnecting: false);
            }
        }

        private async void btnStop_Click(object sender, EventArgs e)
        {
            ToggleControls(isConnecting: true);

            try
            {
                _simulator.Stop();
                await DisconnectAsync();
                SetStatus("Off");
            }
            finally
            {
                ToggleControls(isConnecting: false);
            }
        }

        private async void frmWellSim_FormClosing(object? sender, FormClosingEventArgs e)
        {
            _simulator.Dispose();
            await DisconnectAsync();
        }

        private async Task<Opc.Ua.ApplicationConfiguration> CreateConfigurationAsync()
        {
            string pkiRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Well Sim",
                "pki");

            var configuration = new Opc.Ua.ApplicationConfiguration
            {
                ApplicationName = "Well Simulator",
                ApplicationUri = $"urn:{Utils.GetHostName()}:WellSimulator",
                ApplicationType = ApplicationType.Client,
                SecurityConfiguration = new SecurityConfiguration
                {
                    ApplicationCertificate = new CertificateIdentifier
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(pkiRoot, "own"),
                        SubjectName = "CN=Well Simulator, DC=localhost"
                    },
                    TrustedPeerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(pkiRoot, "trusted", "certs")
                    },
                    TrustedIssuerCertificates = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(pkiRoot, "issuer", "certs")
                    },
                    RejectedCertificateStore = new CertificateTrustList
                    {
                        StoreType = CertificateStoreType.Directory,
                        StorePath = Path.Combine(pkiRoot, "rejected")
                    },
                    AutoAcceptUntrustedCertificates = true,
                    RejectSHA1SignedCertificates = false
                },
                TransportQuotas = new TransportQuotas
                {
                    OperationTimeout = 15_000
                },
                ClientConfiguration = new ClientConfiguration
                {
                    DefaultSessionTimeout = 60_000
                }
            };

            await configuration.ValidateAsync(ApplicationType.Client);
            var application = new ApplicationInstance
            {
                ApplicationName = configuration.ApplicationName,
                ApplicationType = configuration.ApplicationType,
                ApplicationConfiguration = configuration
            };

            bool certificateOk = await application.CheckApplicationInstanceCertificates(false, 0);
            if (!certificateOk)
            {
                throw new InvalidOperationException("The OPC UA application certificate could not be created or loaded.");
            }

            configuration.CertificateValidator.CertificateValidation += (_, e) => e.Accept = true;

            return configuration;
        }

        private async Task<EndpointDescription> SelectEndpointAsync(string endpointUrl)
        {
            string selectedPolicy = GetSelectedSecurityPolicyUri();

            using var discoveryClient = DiscoveryClient.Create(new Uri(endpointUrl));
            var endpoints = await discoveryClient.GetEndpointsAsync(null);

            EndpointDescription? endpoint = endpoints
                .Where(endpoint => EndpointMatchesSecuritySelection(endpoint, selectedPolicy))
                .OrderByDescending(endpoint => endpoint.SecurityMode == MessageSecurityMode.SignAndEncrypt)
                .ThenByDescending(endpoint => endpoint.SecurityLevel)
                .FirstOrDefault();

            if (endpoint is not null)
            {
                return endpoint;
            }

            string securityName = GetSelectedSecurityDisplayName();
            throw new InvalidOperationException($"The server does not expose an endpoint for the selected security mode: {securityName}.");
        }

        private bool EndpointMatchesSecuritySelection(EndpointDescription endpoint, string selectedPolicy)
        {
            if (selectedPolicy == SecurityPolicies.None)
            {
                return endpoint.SecurityMode == MessageSecurityMode.None &&
                       endpoint.SecurityPolicyUri == SecurityPolicies.None;
            }

            return endpoint.SecurityMode != MessageSecurityMode.None &&
                   endpoint.SecurityPolicyUri == selectedPolicy;
        }

        private string GetSelectedSecurityPolicyUri()
        {
            if (radNone.Checked)
            {
                return SecurityPolicies.None;
            }

            if (radAes128_Sha256_RsaOaep.Checked)
            {
                return SecurityPolicies.Aes128_Sha256_RsaOaep;
            }

            if (radAes256_Sha256_RsaPss.Checked)
            {
                return SecurityPolicies.Aes256_Sha256_RsaPss;
            }

            return SecurityPolicies.Basic256Sha256;
        }

        private string GetSelectedSecurityDisplayName()
        {
            if (radNone.Checked)
            {
                return radNone.Text;
            }

            if (radAes128_Sha256_RsaOaep.Checked)
            {
                return radAes128_Sha256_RsaOaep.Text;
            }

            if (radAes256_Sha256_RsaPss.Checked)
            {
                return radAes256_Sha256_RsaPss.Text;
            }

            return radBasic256Sha256.Text;
        }

        private IUserIdentity? CreateUserIdentity()
        {
            string username = txtUsername.Text.Trim();
            if (string.IsNullOrWhiteSpace(username))
            {
                return null;
            }

            return new UserIdentity(username, System.Text.Encoding.UTF8.GetBytes(txtPassword.Text));
        }

        private void StartSimulation()
        {
            _simulator.Start();
        }

        private void Simulator_StatusChanged(string message)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string>(Simulator_StatusChanged), message);
                return;
            }

            SetStatus(message);
        }

        private void Simulator_SnapshotChanged(SimulationSnapshot snapshot)
        {
            //_pendingSnapshots.Enqueue(snapshot);
            //_ = ProcessPendingSnapshotsAsync();
            _latestSnapshot = snapshot;
        }

        private async Task ProcessPendingSnapshotsAsync()
        {
            if (_session?.Connected != true)
            {
                while (_pendingSnapshots.TryDequeue(out _))
                {
                }

                return;
            }

            if (!await _tagWriteGate.WaitAsync(0))
            {
                return;
            }

            try
            {
                SimulationSnapshot? latestSnapshot = null;
                while (_pendingSnapshots.TryDequeue(out SimulationSnapshot? dequeuedSnapshot))
                {
                    latestSnapshot = dequeuedSnapshot;
                }

                if (latestSnapshot is null || _session?.Connected != true)
                {
                    return;
                }

                await WriteSnapshotToIgnitionAsync(_session, latestSnapshot, sendAllWells: !_sentInitialFullSnapshot);
                _sentInitialFullSnapshot = true;
            }
            catch (Exception ex)
            {
                BeginInvoke(() => SetStatus($"Ignition tag write failed: {ex.Message}"));
            }
            finally
            {
                _tagWriteGate.Release();
            }

            if (!_pendingSnapshots.IsEmpty)
            {
                _ = ProcessPendingSnapshotsAsync();
            }
        }

        private async Task StartWriteLoopAsync(Session session, TimeSpan interval)
        {
            CancellationTokenSource cts;
            lock (this)
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                cts = _cts;
            }

            using var timer = new PeriodicTimer(interval);

            try
            {
                while (await timer.WaitForNextTickAsync(cts.Token))
                {
                    var snapshot = _latestSnapshot;
                    if (snapshot == null)
                        continue;

                    if (!await _tagWriteGate.WaitAsync(0, cts.Token))
                    {
                        continue;
                    }

                    try
                    {
                        if (session.Connected)
                        {
                            await WriteSnapshotToIgnitionAsync(session, snapshot, sendAllWells: !_sentInitialFullSnapshot);
                            _sentInitialFullSnapshot = true;
                            await SimulateHmiForValveTransitionsAsync(session, snapshot, cts.Token);
                        }
                    }
                    finally
                    {
                        _tagWriteGate.Release();
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // expected on shutdown
            }
        }

        private async Task LoadUserPinsAsync(Session session)
        {
            _userPinsByRole.Clear();

            // These names match the exported Ignition tags in `Users and Zipper Pins.json`.
            var reads = new (string Role, string TagPath)[]
            {
                ("VT", $"[{IgnitionTagProvider}]Users/VT/VT User 1/Pin"),
                ("WSM", $"[{IgnitionTagProvider}]Users/WSM/WSM User 1/Pin"),
                ("Frac", $"[{IgnitionTagProvider}]Users/Frac/Frac User 1/Pin"),
                ("Wireline", $"[{IgnitionTagProvider}]Users/Wireline/Wireline User 1/Pin"),
            };

            foreach (var (role, path) in reads)
            {
                int? pin = await TryReadInt32Async(session, path);
                if (pin.HasValue)
                {
                    _userPinsByRole[role] = pin.Value;
                }
            }
        }

        private static async Task<int?> TryReadInt32Async(Session session, string ignitionTagPath)
        {
            try
            {
                DataValue dv = await session.ReadValueAsync(new NodeId(ignitionTagPath, IgnitionTagNamespaceIndex));
                if (dv?.Value is null)
                {
                    return null;
                }

                return Convert.ToInt32(dv.Value);
            }
            catch
            {
                return null;
            }
        }

        private async Task SimulateHmiForValveTransitionsAsync(Session session, SimulationSnapshot snapshot, CancellationToken ct)
        {
            // iterate through wells in snapshot
            for (int i = 0; i < snapshot.Wells.Count; i++)
            {
                WellSnapshot well = snapshot.Wells[i];
                int wellNumber = i + 1;

                // did the zipper valve open this snapshot?
                bool zipperOpened = well.Valves.TryGetValue(ValveNames.Zipper, out ValvePositions zipperPos) &&
                                    zipperPos == ValvePositions.Opened;
                // did lower zipper valve open this snapshot?
                bool lowerZipperOpened = well.Valves.TryGetValue(ValveNames.LowerZipper, out ValvePositions lowerPos) &&
                                         lowerPos == ValvePositions.Opened;
                bool isActionComplete;
                if (zipperOpened == lowerZipperOpened)
                {
                    isActionComplete = true;
                }
                else
                {
                    isActionComplete = false;
                }

                if (!_lastValveStatesByWell.TryGetValue(wellNumber, out var last))
                {
                    _lastValveStatesByWell[wellNumber] = (zipperOpened, lowerZipperOpened);
                    continue;
                }

                if (zipperOpened != last.ZipperOpened)
                {
                    await SimulateHmiZipperActionAsync(session, wellNumber, isLowerZipper: false, open: zipperOpened, ct, isActionComplete);
                }

                if (lowerZipperOpened != last.LowerZipperOpened)
                {
                    await SimulateHmiZipperActionAsync(session, wellNumber, isLowerZipper: true, open: lowerZipperOpened, ct, isActionComplete);
                }

                _lastValveStatesByWell[wellNumber] = (zipperOpened, lowerZipperOpened);
            }
        }

        private async Task SimulateHmiZipperActionAsync(Session session, int wellNumber, bool isLowerZipper, bool open, CancellationToken ct, bool isActionComplete)
        {
            string wellBasePath = $"[{IgnitionTagProvider}]{IgnitionPadFolder}/Well{wellNumber}";
            string zipperPinsBase = $"[{IgnitionTagProvider}]Zipper Pins";

            string modeFracPath = $"{wellBasePath}/HMI/Modes/Frac";
            string actionName = isLowerZipper
                ? (open ? "Lower Zipper Open" : "Lower Zipper Close")
                : (open ? "Zipper Open" : "Zipper Close");
            string actionPath = $"{wellBasePath}/HMI/Actions/{actionName}";

            var pins = new
            {
                VT = _userPinsByRole.TryGetValue("VT", out int vt) ? vt : 0,
                WSM = _userPinsByRole.TryGetValue("WSM", out int wsm) ? wsm : 0,
                Frac = _userPinsByRole.TryGetValue("Frac", out int frac) ? frac : 0,
                Wline = _userPinsByRole.TryGetValue("Wireline", out int wline) ? wline : 0,
            };

            // 1) Set Frac mode first.
            await WriteIgnitionTagsAsync(session, new (string Path, object Value)[]
            {
                (modeFracPath, true),
            }, ct);

            // 2) Enter pins into Zipper Pins/* Pin and set * Pin Ok true as they arrive.
            await WriteIgnitionTagsAsync(session, new (string Path, object Value)[]
            {
                ($"{zipperPinsBase}/VT Pin", pins.VT),
                ($"{zipperPinsBase}/VT Pin Ok", pins.VT != 0),
                ($"{zipperPinsBase}/WSM Pin", pins.WSM),
                ($"{zipperPinsBase}/WSM Pin Ok", pins.WSM != 0),
                ($"{zipperPinsBase}/Frac Pin", pins.Frac),
                ($"{zipperPinsBase}/Frac Pin Ok", pins.Frac != 0),
                ($"{zipperPinsBase}/Wline Pin", pins.Wline),
                ($"{zipperPinsBase}/Wline Pin Ok", pins.Wline != 0),
            }, ct);

            // 3) Trigger the valve action.
            await WriteIgnitionTagsAsync(session, new (string Path, object Value)[]
            {
                (actionPath, true),
            }, ct);

            // 4) Clear mode, action, and pins after the "action completes".
            // This is intentionally short so the HMI sees the transition.
            await Task.Delay(TimeSpan.FromMilliseconds(2000), ct);

            await WriteIgnitionTagsAsync(session, new (string Path, object Value)[]
            {
                (modeFracPath, !isActionComplete),
                (actionPath, false),

                ($"{zipperPinsBase}/VT Pin", 0),
                ($"{zipperPinsBase}/VT Pin Ok", false),
                ($"{zipperPinsBase}/WSM Pin", 0),
                ($"{zipperPinsBase}/WSM Pin Ok", false),
                ($"{zipperPinsBase}/Frac Pin", 0),
                ($"{zipperPinsBase}/Frac Pin Ok", false),
                ($"{zipperPinsBase}/Wline Pin", 0),
                ($"{zipperPinsBase}/Wline Pin Ok", false),
            }, ct);
        }

        private static async Task WriteIgnitionTagsAsync(Session session, IReadOnlyList<(string Path, object Value)> writes, CancellationToken ct)
        {
            if (writes.Count == 0)
            {
                return;
            }

            var nodesToWrite = new WriteValueCollection();
            foreach (var (path, value) in writes)
            {
                nodesToWrite.Add(new WriteValue
                {
                    NodeId = new NodeId(path, IgnitionTagNamespaceIndex),
                    AttributeId = Attributes.Value,
                    Value = new DataValue(new Variant(value))
                });
            }

            WriteResponse response = await session.WriteAsync(null, nodesToWrite, ct);
            for (int i = 0; i < response.Results.Count; i++)
            {
                StatusCode status = response.Results[i];
                if (StatusCode.IsBad(status))
                {
                    throw new ServiceResultException(status, $"Ignition write failed for {nodesToWrite[i].NodeId}.");
                }
            }
        }

        private async Task WriteSnapshotToIgnitionAsync(Session session, SimulationSnapshot snapshot, bool sendAllWells)
        {
            var nodesToWrite = new WriteValueCollection();
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Writing snapshot to Ignition... (allWells={sendAllWells})");

            if (sendAllWells)
            {
                for (int index = 0; index < snapshot.Wells.Count; index++)
                {
                    AddWellWrites(nodesToWrite, snapshot, index);
                }
            }
            else
            {
                int activeWellIndex = GetActiveWellIndex(snapshot);
                AddWellWrites(nodesToWrite, snapshot, activeWellIndex);
            }

            if (nodesToWrite.Count == 0)
            {
                return;
            }

            WriteResponse response = await session.WriteAsync(null, nodesToWrite, CancellationToken.None);
            for (int i = 0; i < response.Results.Count; i++)
            {
                StatusCode status = response.Results[i];
                if (StatusCode.IsBad(status))
                {
                    throw new ServiceResultException(status, $"Ignition write failed for {nodesToWrite[i].NodeId}.");
                }
            }

            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Wrote snapshot ({nodesToWrite.Count} tags).");
        }

        private static void AddWellWrites(WriteValueCollection nodesToWrite, SimulationSnapshot snapshot, int wellIndex)
        {
            WellSnapshot well = snapshot.Wells[wellIndex];
            string wellBasePath = $"[{IgnitionTagProvider}]{IgnitionPadFolder}/Well{wellIndex + 1}";

            AddWrite(nodesToWrite, wellBasePath, "Name", well.Name);
            AddWrite(nodesToWrite, wellBasePath, "Last Known Name", well.Name);
            AddWrite(nodesToWrite, wellBasePath, "Mode", GetIgnitionModeValue(well.Mode));
            AddWrite(nodesToWrite, wellBasePath, "Color", (int)well.Color);
            AddWrite(nodesToWrite, wellBasePath, "WellPressure", Convert.ToInt32(Math.Round(well.WellPressure)));
            AddWrite(nodesToWrite, wellBasePath, "PumpdownPressure", Convert.ToInt32(Math.Round(well.PumpDownPressure)));
            AddWrite(nodesToWrite, wellBasePath, "FracPressure", Convert.ToInt32(Math.Round(snapshot.FracPressure)));
            AddWrite(nodesToWrite, wellBasePath, "WirelinePressure", Convert.ToInt32(Math.Round(well.WirelinePressure)));
            AddWrite(nodesToWrite, wellBasePath, "Wireline A Selected", well.CurrentCrew == Crew.A);
            //AddWrite(nodesToWrite, wellBasePath, "Wireline B Selected", well.CurrentCrew == Crew.B);

            AddWrite(nodesToWrite, wellBasePath, "CrownOpened", IsValveOpen(well, ValveNames.Crown));
            AddWrite(nodesToWrite, wellBasePath, "CrownClosed", IsValveClosed(well, ValveNames.Crown));
            AddWrite(nodesToWrite, wellBasePath, "ZipperOpened", IsValveOpen(well, ValveNames.Zipper));
            AddWrite(nodesToWrite, wellBasePath, "ZipperClosed", IsValveClosed(well, ValveNames.Zipper));
            AddWrite(nodesToWrite, wellBasePath, "PDEqualOpened", IsValveOpen(well, ValveNames.Equalizing));
            AddWrite(nodesToWrite, wellBasePath, "PDEqualClosed", IsValveClosed(well, ValveNames.Equalizing));
            AddWrite(nodesToWrite, wellBasePath, "InnerPD Opened", IsValveOpen(well, ValveNames.InnerPumpDown));
            AddWrite(nodesToWrite, wellBasePath, "InnerPD Closed", IsValveClosed(well, ValveNames.InnerPumpDown));
            AddWrite(nodesToWrite, wellBasePath, "OuterPDOpened", IsValveOpen(well, ValveNames.OuterPumpDown));
            AddWrite(nodesToWrite, wellBasePath, "OuterPDClosed", IsValveClosed(well, ValveNames.OuterPumpDown));
            AddWrite(nodesToWrite, wellBasePath, "Inner Flowback Opened", IsValveOpen(well, ValveNames.InnerFlowback));
            AddWrite(nodesToWrite, wellBasePath, "Inner Flowback Closed", IsValveClosed(well, ValveNames.InnerFlowback));
            AddWrite(nodesToWrite, wellBasePath, "Outer Flowback Opened", IsValveOpen(well, ValveNames.OuterFlowback));
            AddWrite(nodesToWrite, wellBasePath, "Outer Flowback Closed", IsValveClosed(well, ValveNames.OuterFlowback));
            AddWrite(nodesToWrite, wellBasePath, "HMV Opened", IsValveOpen(well, ValveNames.Master));
            AddWrite(nodesToWrite, wellBasePath, "HMV Closed", IsValveClosed(well, ValveNames.Master));
            AddWrite(nodesToWrite, wellBasePath, "Lower Zipper Opened", IsValveOpen(well, ValveNames.LowerZipper));
            AddWrite(nodesToWrite, wellBasePath, "Lower Zipper Closed", IsValveClosed(well, ValveNames.LowerZipper));

            AddWrite(nodesToWrite, wellBasePath, "Alarms/Crown_Open", well.Mode == OperationMode.Frac && IsValveOpen(well, ValveNames.Crown));
            AddWrite(nodesToWrite, wellBasePath, "Alarms/Crown_NotClosed", well.Mode == OperationMode.Frac && !IsValveClosed(well, ValveNames.Crown));
            AddWrite(nodesToWrite, wellBasePath, "Alarms/Zipper_Open", well.Mode == OperationMode.Wireline && IsValveOpen(well, ValveNames.Zipper));
            AddWrite(nodesToWrite, wellBasePath, "Alarms/Flowback_NotClosed", well.Mode == OperationMode.Frac &&
                (IsValveOpen(well, ValveNames.InnerFlowback) || IsValveOpen(well, ValveNames.OuterFlowback)));
            AddWrite(nodesToWrite, wellBasePath, "Alarms/PumpDown_NotClosed", well.Mode == OperationMode.Wireline &&
                (IsValveOpen(well, ValveNames.InnerPumpDown) || IsValveOpen(well, ValveNames.OuterPumpDown)));
            AddWrite(nodesToWrite, wellBasePath, "Alarms/Wireline_PumpdownOpen", well.Mode == OperationMode.Wireline &&
                (IsValveOpen(well, ValveNames.InnerPumpDown) || IsValveOpen(well, ValveNames.OuterPumpDown)));
            AddWrite(nodesToWrite, wellBasePath, "Alarms/Wireline_ZipperOpen", well.Mode == OperationMode.Wireline && IsValveOpen(well, ValveNames.Zipper));
            AddWrite(nodesToWrite, wellBasePath, "Alarms/FracWell_NotEqualized", well.Mode == OperationMode.Frac && IsValveClosed(well, ValveNames.Equalizing));
        }

        private static int GetActiveWellIndex(SimulationSnapshot snapshot)
        {
            if (snapshot.Wells.Count == 0)
            {
                return 0;
            }

            // Prefer wells that are clearly "active" first.
            for (int i = 0; i < snapshot.Wells.Count; i++)
            {
                WellSnapshot well = snapshot.Wells[i];
                if (well.Mode != OperationMode.Standby)
                {
                    return i;
                }
            }

            for (int i = 0; i < snapshot.Wells.Count; i++)
            {
                WellSnapshot well = snapshot.Wells[i];
                if (well.CurrentCrew != Crew.None)
                {
                    return i;
                }
            }

            for (int i = 0; i < snapshot.Wells.Count; i++)
            {
                WellSnapshot well = snapshot.Wells[i];
                if (IsValveOpen(well, ValveNames.Zipper) || IsValveOpen(well, ValveNames.LowerZipper))
                {
                    return i;
                }
            }

            return 0;
        }

        private static bool IsValveOpen(WellSnapshot well, ValveNames valve) =>
            well.Valves.TryGetValue(valve, out ValvePositions position) && position == ValvePositions.Opened;

        private static bool IsValveClosed(WellSnapshot well, ValveNames valve) =>
            !well.Valves.TryGetValue(valve, out ValvePositions position) || position == ValvePositions.Closed;

        private static string GetIgnitionModeValue(OperationMode mode) =>
            mode switch
            {
                OperationMode.Frac => "Frac",
                OperationMode.Wireline => "Wireline",
                _ => "Standby"
            };

        private static void AddWrite(WriteValueCollection nodesToWrite, string wellBasePath, string relativeTagPath, object value)
        {
            nodesToWrite.Add(new WriteValue
            {
                NodeId = new NodeId($"{wellBasePath}/{relativeTagPath}", IgnitionTagNamespaceIndex),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(value))
            });
        }

        private async Task DisconnectAsync()
        {
            if (_session is null)
            {
                return;
            }

            CancellationTokenSource? cts;
            lock (this)
            {
                cts = _cts;
                _cts = null;
            }

            if (cts is not null)
            {
                try
                {
                    cts.Cancel();
                }
                finally
                {
                    cts.Dispose();
                }
            }

            Session session = _session;
            _session = null;

            if (session.Connected)
            {
                await session.CloseAsync();
            }

            session.Dispose();
        }

        private void SetStatus(string statusText)
        {
            txtStatus.Text = statusText;
        }

        private void ToggleControls(bool isConnecting)
        {
            btnStart.Enabled = !isConnecting;
            btnStop.Enabled = !isConnecting;
            txtAddress.Enabled = !isConnecting;
            txtPort.Enabled = !isConnecting;
            txtUsername.Enabled = !isConnecting;
            txtPassword.Enabled = !isConnecting;
            radNone.Enabled = !isConnecting;
            radBasic256Sha256.Enabled = !isConnecting;
            radAes128_Sha256_RsaOaep.Enabled = !isConnecting;
            radAes256_Sha256_RsaPss.Enabled = !isConnecting;
        }
    }
}
