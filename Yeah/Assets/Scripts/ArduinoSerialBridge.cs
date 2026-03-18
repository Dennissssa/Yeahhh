using UnityEngine;
using UnityEngine.Events;
using UnityEngine.InputSystem;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

/// <summary>
/// Two-way serial bridge between Unity and the Arduino Uno.
///
/// ── Arduino → Unity  (piezo hits) ────────────────────────────
///   Arduino sends "HIT_1" … "HIT_6" when a piezo is struck.
///   Each fires a UnityEvent in the onHit array on the main thread.
///   Wire onHit[0]…onHit[5] in the Inspector to the same action
///   your keyboard shortcut already calls to fix/clear an object.
///
///   Object 6 (Printer): hitting piezo 6 (A5) stops the printer
///   and clears its anomaly state.
///
/// ── Unity → Arduino  (LED states) ────────────────────────────
///   Attach a WorkItem to each LEDBinding entry. The bridge listens
///   to that WorkItem's existing events and sends the right command:
///     OnBroken       → LED{n}:ANOMALY   (yellow)
///     OnBaiting      → LED{n}:BAIT      (white flash)
///     OnFixed        → LED{n}:NORMAL    (red)
///     OnBaitingEnded → LED{n}:NORMAL    (red)
///
/// ── Unity → Arduino  (printer motor) ─────────────────────────
///   Attach a WorkItem to the PrinterBinding. The bridge listens
///   to that WorkItem's events and sends:
///     OnBroken       → PRINTER:ON    (start printing)
///     OnFixed        → PRINTER:OFF   (stop printing)
///   The printer has no bait state.
///
/// ── Setup ─────────────────────────────────────────────────────
///   1. Attach this script to any persistent GameObject.
///   2. Set portName to your Arduino's port.
///   3. Add one LEDBinding per physical LED ring and drag in its WorkItem.
///   4. Drag the Object 6 WorkItem into the PrinterBinding slot.
///   5. Wire onHit[0]…onHit[5] to the same method(s) your keyboard input calls.
/// </summary>
public class ArduinoSerialBridge : MonoBehaviour
{
    // ── Configuration ─────────────────────────────────────────

    private const string DefaultPortName = "/dev/cu.usbserial-1130"; // Windows: "COM3"
    private const int DefaultBaudRate = 9600;
    private const int ReadTimeoutMs = 100;
    private const int WriteTimeoutMs = 100;
    private const int NumPiezos = 6;

    // ── Inspector ─────────────────────────────────────────────

    [Header("Serial Settings")]
    [Tooltip("Arduino port. Windows: COM3   Mac: /dev/cu.usbserial-...")]
    [SerializeField] private string portName = DefaultPortName;
    [SerializeField] private int baudRate = DefaultBaudRate;

    [Header("Piezo Hit Events  (index 0 = Object 1, index 5 = Object 6 / Printer)")]
    [Tooltip("Wire each slot to the same action your keyboard shortcut calls.")]
    public UnityEvent[] onHit = new UnityEvent[NumPiezos];

    [Header("Block Hit（按住时所有 onHit 不触发）")]
    [Tooltip("按住此键期间，所有 Piezo Hit 不触发（不调用 onHit）。设为 None 则不屏蔽")]
    public KeyCode blockHitKeyCode = KeyCode.None;

    [Header("LED → WorkItem Bindings")]
    [Tooltip("One entry per physical LED ring.")]
    [SerializeField] private LEDBinding[] ledBindings = Array.Empty<LEDBinding>();

    [Header("Printer → WorkItem Binding (Object 6)")]
    [Tooltip("WorkItem for the printer. OnBroken starts printing, OnFixed stops it.")]
    [SerializeField] private WorkItem printerWorkItem;

    // ── Private state ─────────────────────────────────────────

    private SerialPort _serial;
    private Thread _readThread;
    private bool _isRunning;

    private readonly bool[] _hitPending = new bool[NumPiezos];

    private readonly List<BoundListener> _boundListeners = new List<BoundListener>();

    private UnityAction _printerOnBroken;
    private UnityAction _printerOnFixed;

    // ── Lifecycle ─────────────────────────────────────────────

    private void Start()
    {
        OpenSerial();
        if (_serial == null || !_serial.IsOpen) return;

        BindWorkItems();
        BindPrinter();

        foreach (var binding in ledBindings)
            SendLED(binding.LedIndex, LedCommand.Normal);

        SendPrinter(false);
    }

    private void Update()
    {
        bool blockHits = blockHitKeyCode != KeyCode.None && IsBlockKeyHeld();

        for (int i = 0; i < NumPiezos; i++)
        {
            if (!_hitPending[i]) continue;
            _hitPending[i] = false;
            if (blockHits) continue;
            onHit[i]?.Invoke();
        }
    }

    private bool IsBlockKeyHeld()
    {
        if (Keyboard.current == null) return false;
        var key = KeyCodeToKey(blockHitKeyCode);
        return key != Key.None && Keyboard.current[key].isPressed;
    }

    private static Key KeyCodeToKey(KeyCode kc)
    {
        switch (kc)
        {
            case KeyCode.Space: return Key.Space;
            case KeyCode.Return: return Key.Enter;
            case KeyCode.Escape: return Key.Escape;
            case KeyCode.Alpha0: return Key.Digit0;
            case KeyCode.Alpha1: return Key.Digit1;
            case KeyCode.Alpha2: return Key.Digit2;
            case KeyCode.Alpha3: return Key.Digit3;
            case KeyCode.Alpha4: return Key.Digit4;
            case KeyCode.Alpha5: return Key.Digit5;
            case KeyCode.Alpha6: return Key.Digit6;
            case KeyCode.Alpha7: return Key.Digit7;
            case KeyCode.Alpha8: return Key.Digit8;
            case KeyCode.Alpha9: return Key.Digit9;
            case KeyCode.A: return Key.A;
            case KeyCode.B: return Key.B;
            case KeyCode.C: return Key.C;
            case KeyCode.D: return Key.D;
            case KeyCode.E: return Key.E;
            case KeyCode.F: return Key.F;
            case KeyCode.G: return Key.G;
            case KeyCode.H: return Key.H;
            case KeyCode.I: return Key.I;
            case KeyCode.J: return Key.J;
            case KeyCode.K: return Key.K;
            case KeyCode.L: return Key.L;
            case KeyCode.M: return Key.M;
            case KeyCode.N: return Key.N;
            case KeyCode.O: return Key.O;
            case KeyCode.P: return Key.P;
            case KeyCode.Q: return Key.Q;
            case KeyCode.R: return Key.R;
            case KeyCode.S: return Key.S;
            case KeyCode.T: return Key.T;
            case KeyCode.U: return Key.U;
            case KeyCode.V: return Key.V;
            case KeyCode.W: return Key.W;
            case KeyCode.X: return Key.X;
            case KeyCode.Y: return Key.Y;
            case KeyCode.Z: return Key.Z;
            case KeyCode.LeftShift: return Key.LeftShift;
            case KeyCode.RightShift: return Key.RightShift;
            case KeyCode.LeftControl: return Key.LeftCtrl;
            case KeyCode.RightControl: return Key.RightCtrl;
            case KeyCode.LeftAlt: return Key.LeftAlt;
            case KeyCode.RightAlt: return Key.RightAlt;
            default: return Key.None;
        }
    }

    private void OnDestroy()
    {
        UnbindWorkItems();
        UnbindPrinter();

        _isRunning = false;
        _readThread?.Join(500);

        try
        {
            if (_serial != null && _serial.IsOpen)
                _serial.Close();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ArduinoSerialBridge] Port close error: {ex.Message}");
        }
    }

    // ── Serial setup ──────────────────────────────────────────

    private void OpenSerial()
    {
        if (string.IsNullOrEmpty(portName))
        {
            Debug.LogWarning("[ArduinoSerialBridge] No port configured.");
            return;
        }

        try
        {
            _serial = new SerialPort(portName, baudRate)
            {
                ReadTimeout = ReadTimeoutMs,
                WriteTimeout = WriteTimeoutMs,
                NewLine = "\n"
            };
            _serial.Open();

            _isRunning = true;
            _readThread = new Thread(ReadLoop) { IsBackground = true };
            _readThread.Start();

            Debug.Log($"[ArduinoSerialBridge] Opened {portName} at {baudRate} baud.");
        }
        catch (Exception ex)
        {
            Debug.LogError(
                $"[ArduinoSerialBridge] Could not open '{portName}': {ex.Message}\n" +
                "Check the port name and close the Arduino Serial Monitor if it is open.");
        }
    }

    // ── WorkItem binding ──────────────────────────────────────

    private void BindWorkItems()
    {
        foreach (var binding in ledBindings)
        {
            if (binding.WorkItem == null) continue;

            int idx = binding.LedIndex;

            UnityAction onBroken = () => SendLED(idx, LedCommand.Anomaly);
            UnityAction onBaiting = () => SendLED(idx, LedCommand.Bait);
            UnityAction onFixed = () => SendLED(idx, LedCommand.Normal);
            UnityAction onBaitingEnded = () => SendLED(idx, LedCommand.Normal);

            binding.WorkItem.OnBroken.AddListener(onBroken);
            binding.WorkItem.OnBaiting.AddListener(onBaiting);
            binding.WorkItem.OnFixed.AddListener(onFixed);
            binding.WorkItem.OnBaitingEnded.AddListener(onBaitingEnded);

            _boundListeners.Add(new BoundListener(
                binding.WorkItem, onBroken, onBaiting, onFixed, onBaitingEnded));
        }
    }

    private void UnbindWorkItems()
    {
        foreach (var entry in _boundListeners)
        {
            if (entry.WorkItem == null) continue;
            entry.WorkItem.OnBroken.RemoveListener(entry.OnBroken);
            entry.WorkItem.OnBaiting.RemoveListener(entry.OnBaiting);
            entry.WorkItem.OnFixed.RemoveListener(entry.OnFixed);
            entry.WorkItem.OnBaitingEnded.RemoveListener(entry.OnBaitingEnded);
        }
        _boundListeners.Clear();
    }

    // ── Printer binding ───────────────────────────────────────

    private void BindPrinter()
    {
        if (printerWorkItem == null) return;

        _printerOnBroken = () => SendPrinter(true);
        _printerOnFixed = () => SendPrinter(false);

        printerWorkItem.OnBroken.AddListener(_printerOnBroken);
        printerWorkItem.OnFixed.AddListener(_printerOnFixed);
    }

    private void UnbindPrinter()
    {
        if (printerWorkItem == null) return;

        if (_printerOnBroken != null)
            printerWorkItem.OnBroken.RemoveListener(_printerOnBroken);
        if (_printerOnFixed != null)
            printerWorkItem.OnFixed.RemoveListener(_printerOnFixed);
    }

    // ── Public API ────────────────────────────────────────────

    /// <summary>
    /// Sends an LED state command to the Arduino.
    /// ledIndex: 1 = pin 2 (Object 1)   2 = pin 3 (Object 4)
    /// </summary>
    public void SendLED(int ledIndex, string command)
    {
        if (_serial == null || !_serial.IsOpen) return;

        string msg = $"LED{ledIndex}:{command}";
        try
        {
            _serial.WriteLine(msg);
            Debug.Log($"[ArduinoSerialBridge] Sent: {msg}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ArduinoSerialBridge] Write error: {ex.Message}");
        }
    }

    /// <summary>
    /// true = PRINTER:ON   false = PRINTER:OFF
    /// </summary>
    public void SendPrinter(bool on)
    {
        if (_serial == null || !_serial.IsOpen) return;

        string msg = on ? "PRINTER:ON" : "PRINTER:OFF";
        try
        {
            _serial.WriteLine(msg);
            Debug.Log($"[ArduinoSerialBridge] Sent: {msg}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ArduinoSerialBridge] Write error: {ex.Message}");
        }
    }

    // ── Background read thread ────────────────────────────────

    private void ReadLoop()
    {
        while (_isRunning)
        {
            try
            {
                string line = _serial.ReadLine().Trim();

                if (line.StartsWith("HIT_") &&
                    int.TryParse(line.Substring(4), out int piezoNumber))
                {
                    int arrayIndex = piezoNumber - 1;
                    if (arrayIndex >= 0 && arrayIndex < NumPiezos)
                        _hitPending[arrayIndex] = true;
                }
            }
            catch (TimeoutException) { /* expected during idle */ }
            catch (Exception ex)
            {
                Debug.LogError($"[ArduinoSerialBridge] Read error: {ex.Message}");
                _isRunning = false;
            }
        }
    }

    public static class LedCommand
    {
        public const string Normal = "NORMAL";
        public const string Anomaly = "ANOMALY";
        public const string Bait = "BAIT";
    }

    [Serializable]
    public class LEDBinding
    {
        [Tooltip("1 = LED ring on pin 2 (Object 1)   2 = LED ring on pin 3 (Object 4)")]
        public int LedIndex = 1;

        [Tooltip("WorkItem this LED should follow. Leave null for manual control.")]
        public WorkItem WorkItem;
    }

    private class BoundListener
    {
        public readonly WorkItem WorkItem;
        public readonly UnityAction OnBroken;
        public readonly UnityAction OnBaiting;
        public readonly UnityAction OnFixed;
        public readonly UnityAction OnBaitingEnded;

        public BoundListener(WorkItem item,
                             UnityAction onBroken,
                             UnityAction onBaiting,
                             UnityAction onFixed,
                             UnityAction onBaitingEnded)
        {
            WorkItem = item;
            OnBroken = onBroken;
            OnBaiting = onBaiting;
            OnFixed = onFixed;
            OnBaitingEnded = onBaitingEnded;
        }
    }
}
