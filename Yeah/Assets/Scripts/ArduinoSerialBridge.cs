using UnityEngine;
using UnityEngine.Events;
using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

/// <summary>
/// Two-way serial bridge between Unity and the Arduino Uno.
///
/// ── Arduino → Unity  (piezo hits) ────────────────────────────
///   Arduino sends "HIT_1" … "HIT_5" when a piezo is struck.
///   Each fires a UnityEvent (onHit1 … onHit5) on the main thread.
///   Wire these in the Inspector to the SAME action your keyboard
///   shortcut already calls to fix/clear an object — the game logic
///   underneath does not need to change at all.
///
/// ── Unity → Arduino  (LED states) ────────────────────────────
///   Attach a WorkItem to each LEDBinding entry.  The bridge listens
///   to that WorkItem's existing events and sends the right LED
///   command automatically:
///     OnBroken       → LED{n}:ANOMALY   (yellow)
///     OnBaiting      → LED{n}:BAIT      (white flash)
///     OnFixed        → LED{n}:NORMAL    (red)
///     OnBaitingEnded → LED{n}:NORMAL    (red)
///
/// ── Setup ─────────────────────────────────────────────────────
///   1. Attach this script to any GameObject.
///   2. Set portName to your Arduino's port.
///   3. Add one LEDBinding per physical LED ring, set its LedIndex
///      (1 or 2) and drag in the WorkItem it should follow.
///   4. Wire onHit1 … onHit5 to the same method(s) your keyboard
///      input already calls.
/// </summary>
public class ArduinoSerialBridge : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────

    [Header("Serial Settings")]
    [Tooltip("Arduino port. Windows: COM3  Mac: /dev/cu.usbserial-...")]
    [SerializeField] private string portName = "/dev/cu.usbserial-1130";
    [SerializeField] private int baudRate = 9600;

    [Header("Piezo Hit Events")]
    [Tooltip("Wire each event to the same action your keyboard shortcut calls.")]
    public UnityEvent onHit1;
    public UnityEvent onHit2;
    public UnityEvent onHit3;
    public UnityEvent onHit4;
    public UnityEvent onHit5;

    [Header("LED → WorkItem Bindings")]
    [Tooltip("One entry per physical LED ring. Set LedIndex to 1 or 2.")]
    [SerializeField] private LEDBinding[] ledBindings = Array.Empty<LEDBinding>();

    // ── Private ───────────────────────────────────────────────

    private SerialPort serial;
    private Thread readThread;
    private bool isRunning = false;

    // Flags set by background thread, consumed by Update() on main thread
    private volatile bool hit1Pending = false;
    private volatile bool hit2Pending = false;
    private volatile bool hit3Pending = false;
    private volatile bool hit4Pending = false;
    private volatile bool hit5Pending = false;

    // Track listeners so we can remove them on destroy
    private readonly List<BoundListener> boundListeners = new List<BoundListener>();

    // ── Lifecycle ─────────────────────────────────────────────

    private void Start()
    {
        OpenSerial();
        if (serial == null || !serial.IsOpen) return;

        BindWorkItems();

        // Initialise all bound LEDs to NORMAL (red) on game start
        foreach (var binding in ledBindings)
            SendLED(binding.LedIndex, "NORMAL");
    }

    private void Update()
    {
        // Unity API calls must happen on the main thread.
        // Background thread sets flags; Update() fires the events.
        if (hit1Pending) { hit1Pending = false; onHit1?.Invoke(); }
        if (hit2Pending) { hit2Pending = false; onHit2?.Invoke(); }
        if (hit3Pending) { hit3Pending = false; onHit3?.Invoke(); }
        if (hit4Pending) { hit4Pending = false; onHit4?.Invoke(); }
        if (hit5Pending) { hit5Pending = false; onHit5?.Invoke(); }
    }

    private void OnDestroy()
    {
        // Remove all WorkItem listeners
        foreach (var entry in boundListeners)
        {
            if (entry.WorkItem == null) continue;
            entry.WorkItem.OnBroken.RemoveListener(entry.OnBroken);
            entry.WorkItem.OnBaiting.RemoveListener(entry.OnBaiting);
            entry.WorkItem.OnFixed.RemoveListener(entry.OnFixed);
            entry.WorkItem.OnBaitingEnded.RemoveListener(entry.OnBaitingEnded);
        }
        boundListeners.Clear();

        isRunning = false;
        readThread?.Join(500);

        try
        {
            if (serial != null && serial.IsOpen)
                serial.Close();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[ArduinoSerialBridge] Close port error: {ex.Message}");
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
            serial = new SerialPort(portName, baudRate)
            {
                ReadTimeout  = 100,
                WriteTimeout = 100,
                NewLine      = "\n"
            };
            serial.Open();
            isRunning  = true;
            readThread = new Thread(ReadLoop) { IsBackground = true };
            readThread.Start();
            Debug.Log($"[ArduinoSerialBridge] Opened {portName} at {baudRate} baud.");
        }
        catch (Exception ex)
        {
            Debug.LogError($"[ArduinoSerialBridge] Could not open '{portName}': {ex.Message}\n" +
                           "Check the port name and close the Arduino Serial Monitor if open.");
        }
    }

    // ── WorkItem binding ──────────────────────────────────────

    private void BindWorkItems()
    {
        foreach (var binding in ledBindings)
        {
            if (binding.WorkItem == null) continue;

            int ledIdx = binding.LedIndex;   // capture for lambda

            UnityAction onBroken       = () => SendLED(ledIdx, "ANOMALY");
            UnityAction onBaiting      = () => SendLED(ledIdx, "BAIT");
            UnityAction onFixed        = () => SendLED(ledIdx, "NORMAL");
            UnityAction onBaitingEnded = () => SendLED(ledIdx, "NORMAL");

            binding.WorkItem.OnBroken.AddListener(onBroken);
            binding.WorkItem.OnBaiting.AddListener(onBaiting);
            binding.WorkItem.OnFixed.AddListener(onFixed);
            binding.WorkItem.OnBaitingEnded.AddListener(onBaitingEnded);

            boundListeners.Add(new BoundListener(
                binding.WorkItem, onBroken, onBaiting, onFixed, onBaitingEnded));
        }
    }

    // ── Public API ────────────────────────────────────────────

    /// <summary>
    /// Send an LED state command directly.
    /// command: "NORMAL" | "ANOMALY" | "BAIT"
    /// ledIndex: 1 (Object 1, pin 2) or 2 (Object 4, pin 7)
    /// </summary>
    public void SendLED(int ledIndex, string command)
    {
        if (serial == null || !serial.IsOpen) return;

        string msg = $"LED{ledIndex}:{command}";
        try
        {
            serial.WriteLine(msg);
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
        while (isRunning)
        {
            try
            {
                string line = serial.ReadLine().Trim();

                switch (line)
                {
                    case "HIT_1": hit1Pending = true; break;
                    case "HIT_2": hit2Pending = true; break;
                    case "HIT_3": hit3Pending = true; break;
                    case "HIT_4": hit4Pending = true; break;
                    case "HIT_5": hit5Pending = true; break;
                }
            }
            catch (TimeoutException) { /* normal during idle */ }
            catch (Exception ex)
            {
                Debug.LogError($"[ArduinoSerialBridge] Read error: {ex.Message}");
                isRunning = false;
            }
        }
    }

    // ── Data types ────────────────────────────────────────────

    /// <summary>
    /// Binds one physical LED ring to a WorkItem.
    /// The LED automatically follows the WorkItem's Broken/Bait/Fixed states.
    /// </summary>
    [Serializable]
    public class LEDBinding
    {
        [Tooltip("1 = LED ring on pin 2 (Object 1)   2 = LED ring on pin 7 (Object 4)")]
        public int LedIndex = 1;

        [Tooltip("The WorkItem this LED should follow. Leave empty for manual control only.")]
        public WorkItem WorkItem;
    }

    private struct BoundListener
    {
        public WorkItem    WorkItem;
        public UnityAction OnBroken;
        public UnityAction OnBaiting;
        public UnityAction OnFixed;
        public UnityAction OnBaitingEnded;

        public BoundListener(WorkItem item, UnityAction onBroken, UnityAction onBaiting,
                             UnityAction onFixed, UnityAction onBaitingEnded)
        {
            WorkItem       = item;
            OnBroken       = onBroken;
            OnBaiting      = onBaiting;
            OnFixed        = onFixed;
            OnBaitingEnded = onBaitingEnded;
        }
    }
}
