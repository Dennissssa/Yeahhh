using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance { get; private set; }

    public static bool FreezeFailures { get; private set; } = false;

    [Header("Work Value（无阶段配置时作为默认值；有阶段时由阶段覆盖）")]
    public float work = 0f;
    public float maxWork = 100f;
    public float workPunishment;
    public float workUltraPunishment;
    public float workMashGain;
    public float workGainPerSecondPerWorkingItem = 1f;
    public float workLossPerSecondPerBrokenItem = 3f;
    public float bossMinWorkThreshold = 20f;

    [Header("Boss Timing")]
    public float bossMinArriveInterval = 10f;
    public float bossMaxArriveInterval = 25f;
    public float bossWarningDuration = 3f;
    public float bossStayDuration = 6f;

    [Header("教程")]
    [Tooltip("开启后：按顺序逐个 Broke WorkItem，全部修好后立即开始第一次 Boss（不等待 Boss 到来间隔）；Boss 离开后进入正常阶段流程")]
    public bool enableTutorial = true;

    [Tooltip("教程 Broke 顺序；留空则使用下方 Items 列表顺序")]
    public List<WorkItem> tutorialBreakOrder = new List<WorkItem>();

    [Header("正常流程阶段")]
    [Tooltip("教程结束且第一次 Boss 离开后应用第 0 项；之后每经历一次 Boss 检查并离开后进入下一项。无配置则保持当前 Inspector 数值且不触发胜利")]
    public List<GamePhaseConfig> gamePhases = new List<GamePhaseConfig>();

    [Header("References")]
    public UIManager ui;
    public ScreenVignetteTint screenTint;

    [Header("Boss 事件（可选）")]
    public UnityEvent OnBossWarningStarted;
    public UnityEvent OnBossArrived;
    public UnityEvent OnBossLeft;

    [Tooltip("因 Boss 检查导致游戏失败时触发")]
    public UnityEvent OnGameOverBossCaused;

    [Header("Optional")]
    public bool autoFindItemsOnStart = true;

    public List<WorkItem> items = new List<WorkItem>();

    [Header("损坏提示 UI（槽位 + 滑入）")]
    [Tooltip("含 TextMeshProUGUI 的条目预制体，将作为子物体生成到下方列表中的槽位下")]
    public GameObject warningEntryPrefab;

    [Tooltip("场景中 UI 上的空 RectTransform 槽位；始终使用列表中从前到后第一个「未被占用」的槽位（含延迟中的预留）")]
    public List<RectTransform> warningSlotRects = new List<RectTransform>();

    [Tooltip("滑入起点 = 预制体默认 anchoredPosition + 该偏移（槽位本地空间）。例如 (400,0) 从右侧、(0,-80) 从下方")]
    public Vector2 warningSlideInFromOffset = new Vector2(320f, 0f);

    [Tooltip("滑入动画时长（秒）；0 则直接出现在终点")]
    [Min(0f)]
    public float warningSlideInDuration = 0.35f;

    [Tooltip("0~1 进度曲线；留空或无关键帧时用线性")]
    public AnimationCurve warningSlideInEase = AnimationCurve.EaseInOut(0f, 0f, 1f, 1f);

    [Header("损坏提示（教程 / 尚无阶段配置时）")]
    [Min(0f)]
    public float tutorialWarningShowDelayAfterBreak = 0.5f;

    [Tooltip("文案模板；{0} = 物品显示名")]
    public string tutorialWarningMessageFormat = "{0} is broken!";

    /// <summary>各阶段在 Boss 来检查时累计的 Performance 总分（不含教程段）。</summary>
    public float TotalPerformanceScore { get; private set; }

    /// <summary>当前阶段剩余分数（仅正常计分阶段有效）。</summary>
    public float CurrentPhaseScore { get; private set; }

    public bool IsVictory { get; private set; }

    public bool IsGameOver => isGameOver;

    public ArduinoSerialBridge arduinoBridgeScript;

    public bool BossIsHere { get; private set; } = false;
    public bool BossWarning { get; private set; } = false;
    public float BossWarningTimeLeft { get; private set; } = 0f;

    int _activeMaxConcurrentBroken = int.MaxValue;
    float _activeBreakMin = 4f;
    float _activeBreakMax = 10f;
    float _activeScoreLossPerBrokenPerSecond;
    float _activeWarningShowDelayAfterBreak = 0.5f;
    string _activeWarningMessageFormat = "{0} is broken!";

    sealed class BrokenWarningState
    {
        public Coroutine DelayRoutine;
        public Coroutine SlideRoutine;
        public GameObject Instance;
        /// <summary>延迟等待期间即占用，避免多条抢同一槽位。</summary>
        public RectTransform ReservedSlot;
    }

    readonly Dictionary<WorkItem, BrokenWarningState> _brokenWarnings = new Dictionary<WorkItem, BrokenWarningState>();

    int _currentPhaseIndex;
    bool _tutorialBreakSequenceDone;
    /// <summary>第一次 Boss 离开后为 true：开启随机故障（仅教程开局会关）。</summary>
    bool _allowRandomWorkItemFailures;
    bool _firstBossLeaveHandled;
    bool _normalPerformanceScoringActive;

    /// <summary>教程最后一个 WorkItem 修好后的第一次 Boss：跳过 bossMin/MaxArriveInterval 随机等待。</summary>
    bool _immediateBossAfterTutorial;

    float surviveTime;
    bool isGameOver;
    Coroutine _bossLoopCoroutine;
    Coroutine _tutorialCoroutine;

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void OnDestroy()
    {
        ClearAllBrokenWarnings();
    }

    void Start()
    {
        work = Mathf.Clamp(work, 0f, maxWork);

        if (autoFindItemsOnStart && items.Count == 0)
        {
            items.Clear();
            items.AddRange(FindObjectsOfType<WorkItem>());
        }

        _activeMaxConcurrentBroken = int.MaxValue;
        _activeBreakMin = 4f;
        _activeBreakMax = 10f;
        _activeScoreLossPerBrokenPerSecond = 0f;
        CurrentPhaseScore = 0f;
        TotalPerformanceScore = 0f;
        _allowRandomWorkItemFailures = !enableTutorial;
        _activeWarningShowDelayAfterBreak = Mathf.Max(0f, tutorialWarningShowDelayAfterBreak);
        _activeWarningMessageFormat = string.IsNullOrEmpty(tutorialWarningMessageFormat)
            ? "{0} is broken!"
            : tutorialWarningMessageFormat;

        if (ui != null)
        {
            ui.InitWorkSlider(maxWork);
            ui.SetWork(work);
            ui.SetWorkBossMinThresholdIndicator(bossMinWorkThreshold, maxWork);
            ui.SetTime(0f);
            ui.HideBossCountdown();
            ui.HideGameOver();
            ui.HideGameWin();
        }

        if (screenTint != null)
            screenTint.SetTarget(0f, 0.01f);

        FreezeFailures = false;

        if (enableTutorial)
        {
            SetAllWorkItemsAutoBreak(false);
            _tutorialCoroutine = StartCoroutine(TutorialBreakSequenceCoroutine());
        }
        else
            _tutorialBreakSequenceDone = true;

        _bossLoopCoroutine = StartCoroutine(BossLoop());
    }

    void Update()
    {
        if (isGameOver || IsVictory) return;

        surviveTime += Time.deltaTime;

        int working = 0;
        int broken = 0;
        int baiting = 0;

        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] == null) continue;

            WorkItem wi = items[i];
            if (wi.IsBroken)
            {
                broken++;
                if (!_brokenWarnings.ContainsKey(wi))
                    StartBrokenWarningForItem(wi);
            }
            else
            {
                if (_brokenWarnings.ContainsKey(wi))
                    ClearBrokenWarningForItem(wi);
                if (wi.IsBaiting) baiting++;
                else working++;
            }
        }

        if (work < maxWork)
        {
            work += working * workGainPerSecondPerWorkingItem * Time.deltaTime;
            if (work > maxWork) work = maxWork;
        }

        work -= broken * workLossPerSecondPerBrokenItem * Time.deltaTime;
        if (work < 0f) work = 0f;

        if (_normalPerformanceScoringActive && !BossWarning && !BossIsHere)
        {
            CurrentPhaseScore -= broken * _activeScoreLossPerBrokenPerSecond * Time.deltaTime;
            if (CurrentPhaseScore < 0f) CurrentPhaseScore = 0f;
        }

        if (BossIsHere)
        {
            if (broken > 0)
                GameOver("Boss saw broken items!");
            else if (work < bossMinWorkThreshold)
                GameOver("Work is too low!");
        }

        if (ui != null)
        {
            ui.SetWork(work);
            ui.SetTime(surviveTime);

            if (BossWarning) ui.SetBossCountdown(BossWarningTimeLeft);
            else ui.HideBossCountdown();
        }
    }

    IEnumerator TutorialBreakSequenceCoroutine()
    {
        yield return null;

        List<WorkItem> order = tutorialBreakOrder != null && tutorialBreakOrder.Count > 0
            ? tutorialBreakOrder
            : items;

        for (int i = 0; i < order.Count; i++)
        {
            WorkItem wi = order[i];
            if (wi == null) continue;

            while (wi.IsBaiting)
                yield return null;

            wi.Break();

            yield return new WaitUntil(() => !wi.IsBroken && !wi.IsBaiting);
        }

        _immediateBossAfterTutorial = true;
        _tutorialBreakSequenceDone = true;
        _tutorialCoroutine = null;
    }

    IEnumerator BossLoop()
    {
        while (!isGameOver && !IsVictory)
        {
            if (enableTutorial && !_tutorialBreakSequenceDone)
            {
                yield return null;
                continue;
            }

            float wait = Random.Range(bossMinArriveInterval, bossMaxArriveInterval);
            if (_immediateBossAfterTutorial)
            {
                _immediateBossAfterTutorial = false;
                wait = 0f;
            }

            yield return new WaitForSeconds(wait);
            if (isGameOver || IsVictory) yield break;

            FreezeFailures = true;

            BossWarning = true;
            BossWarningTimeLeft = bossWarningDuration;
            OnBossWarningStarted?.Invoke();

            if (screenTint != null)
                screenTint.SetTarget(1f, bossWarningDuration);

            while (BossWarningTimeLeft > 0f && !isGameOver && !IsVictory)
            {
                BossWarningTimeLeft -= Time.deltaTime;
                yield return null;
            }

            BossWarning = false;
            BossWarningTimeLeft = 0f;

            if (isGameOver || IsVictory) yield break;

            BossIsHere = true;
            OnBossArrived?.Invoke();

            if (_normalPerformanceScoringActive)
                TotalPerformanceScore += CurrentPhaseScore;

            if (screenTint != null)
                screenTint.SetTarget(0.35f, 0.15f);

            float stay = bossStayDuration;
            while (stay > 0f && !isGameOver && !IsVictory)
            {
                stay -= Time.deltaTime;
                yield return null;
            }

            BossIsHere = false;
            OnBossLeft?.Invoke();

            FreezeFailures = false;

            if (screenTint != null)
                screenTint.SetTarget(0f, 0.6f);

            if (isGameOver || IsVictory) yield break;

            HandleBossLeftPhaseTransition();
        }
    }

    void HandleBossLeftPhaseTransition()
    {
        if (!_firstBossLeaveHandled)
        {
            _firstBossLeaveHandled = true;
            if (enableTutorial)
            {
                _allowRandomWorkItemFailures = true;
                SetAllWorkItemsAutoBreak(true);
            }

            if (gamePhases != null && gamePhases.Count > 0)
            {
                _currentPhaseIndex = 0;
                ApplyPhaseConfig(gamePhases[0]);
                CurrentPhaseScore = gamePhases[0].phaseBaseScore;
                _normalPerformanceScoringActive = true;
            }
            else
                _normalPerformanceScoringActive = false;

            return;
        }

        if (gamePhases == null || gamePhases.Count == 0)
            return;

        _currentPhaseIndex++;
        if (_currentPhaseIndex >= gamePhases.Count)
        {
            TriggerVictory();
            return;
        }

        ApplyPhaseConfig(gamePhases[_currentPhaseIndex]);
        CurrentPhaseScore = gamePhases[_currentPhaseIndex].phaseBaseScore;
    }

    void ApplyPhaseConfig(GamePhaseConfig c)
    {
        if (c == null) return;

        _activeMaxConcurrentBroken = Mathf.Max(1, c.maxConcurrentBrokenWorkItems);
        _activeBreakMin = c.minBreakIntervalSeconds;
        _activeBreakMax = Mathf.Max(c.minBreakIntervalSeconds, c.maxBreakIntervalSeconds);
        bossMinWorkThreshold = c.bossMinWorkThreshold;
        workPunishment = c.workPunishment;
        workUltraPunishment = c.workUltraPunishment;
        workGainPerSecondPerWorkingItem = c.workGainPerSecondPerWorkingItem;
        workLossPerSecondPerBrokenItem = c.workLossPerSecondPerBrokenItem;
        _activeScoreLossPerBrokenPerSecond = c.scoreLossPerSecondPerBrokenItem;
        _activeWarningShowDelayAfterBreak = Mathf.Max(0f, c.warningShowDelayAfterBreak);
        _activeWarningMessageFormat = string.IsNullOrEmpty(c.warningMessageFormat)
            ? "{0} is broken!"
            : c.warningMessageFormat;

        if (ui != null)
            ui.SetWorkBossMinThresholdIndicator(bossMinWorkThreshold, maxWork);
    }

    /// <summary>为 true 时 WorkItem 自动故障间隔使用阶段配置，否则使用各 WorkItem 自身 Inspector 数值。</summary>
    public bool UsePhaseBreakTiming()
    {
        return _firstBossLeaveHandled && gamePhases != null && gamePhases.Count > 0;
    }

    /// <summary>供 WorkItem 自动故障：当前是否还能新增一个 Broke（不含 Bait）。</summary>
    public bool CanStartNewBrokeState()
    {
        if (isGameOver || IsVictory) return false;

        int broken = 0;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] != null && items[i].IsBroken) broken++;
        }
        return broken < _activeMaxConcurrentBroken;
    }

    public float GetActiveBreakIntervalMin() => _activeBreakMin;
    public float GetActiveBreakIntervalMax() => _activeBreakMax;

    void SetAllWorkItemsAutoBreak(bool on)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i] != null)
                items[i].enableAutoBreak = on;
        }
    }

    void TriggerVictory()
    {
        if (isGameOver || IsVictory) return;
        IsVictory = true;

        if (_bossLoopCoroutine != null)
        {
            StopCoroutine(_bossLoopCoroutine);
            _bossLoopCoroutine = null;
        }
        if (_tutorialCoroutine != null)
        {
            StopCoroutine(_tutorialCoroutine);
            _tutorialCoroutine = null;
        }

        BossWarning = false;
        BossIsHere = false;
        FreezeFailures = false;

        if (screenTint != null)
            screenTint.SetTarget(0f, 0.1f);

        ClearAllBrokenWarnings();

        if (ui != null)
            ui.ShowGameWin(TotalPerformanceScore);

        Time.timeScale = 0f;
    }

    public void Punishment()
    {
        work -= workPunishment;
    }

    public void UltraPunishment()
    {
        work -= workUltraPunishment;
    }

    public void Reward()
    {
        work += workMashGain;
    }

    public void RegisterItem(WorkItem item)
    {
        if (item == null) return;
        if (!items.Contains(item)) items.Add(item);
        item.enableAutoBreak = _allowRandomWorkItemFailures;
    }

    public void UnregisterItem(WorkItem item)
    {
        if (item == null) return;
        items.Remove(item);
        ClearBrokenWarningForItem(item);
    }

    static string WorkItemDisplayName(WorkItem w)
    {
        if (w == null) return "";
        if (!string.IsNullOrWhiteSpace(w.itemName)) return w.itemName.Trim();
        return w.name;
    }

    void StartBrokenWarningForItem(WorkItem item)
    {
        if (item == null || warningEntryPrefab == null || warningSlotRects == null || warningSlotRects.Count == 0)
            return;

        var state = new BrokenWarningState();
        _brokenWarnings[item] = state;
        TryReserveFirstFreeWarningSlot(state);
        state.DelayRoutine = StartCoroutine(BrokenWarningDelayRoutine(item, state));
    }

    static bool IsWarningSlotUsedByAnotherState(RectTransform slot, BrokenWarningState except, Dictionary<WorkItem, BrokenWarningState> map)
    {
        foreach (KeyValuePair<WorkItem, BrokenWarningState> kv in map)
        {
            BrokenWarningState st = kv.Value;
            if (st == except) continue;
            if (st.ReservedSlot == slot) return true;
            if (st.Instance != null && st.Instance.transform.parent == slot) return true;
        }

        return false;
    }

    /// <summary>从列表头开始找第一个空闲槽并写入 state.ReservedSlot；已有预留则不再改。</summary>
    bool TryReserveFirstFreeWarningSlot(BrokenWarningState state)
    {
        if (state == null || warningSlotRects == null || warningSlotRects.Count == 0)
            return false;
        if (state.ReservedSlot != null)
            return true;

        for (int i = 0; i < warningSlotRects.Count; i++)
        {
            RectTransform s = warningSlotRects[i];
            if (s == null) continue;
            if (IsWarningSlotUsedByAnotherState(s, state, _brokenWarnings))
                continue;
            state.ReservedSlot = s;
            return true;
        }

        return false;
    }

    IEnumerator BrokenWarningDelayRoutine(WorkItem item, BrokenWarningState state)
    {
        float delay = Mathf.Max(0f, _activeWarningShowDelayAfterBreak);
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        state.DelayRoutine = null;

        if (item == null || !item.IsBroken)
        {
            _brokenWarnings.Remove(item);
            yield break;
        }

        while (state.ReservedSlot == null)
        {
            if (!TryReserveFirstFreeWarningSlot(state))
                yield return null;
        }

        RectTransform slot = state.ReservedSlot;
        if (warningEntryPrefab == null || slot == null)
        {
            _brokenWarnings.Remove(item);
            yield break;
        }

        GameObject entry = Instantiate(warningEntryPrefab, slot);
        state.Instance = entry;

        RectTransform entryRt = entry.GetComponent<RectTransform>();
        if (entryRt != null)
        {
            Vector2 endPos = entryRt.anchoredPosition;
            Vector2 startPos = endPos + warningSlideInFromOffset;
            entryRt.anchoredPosition = startPos;

            TMP_Text tmp = entry.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null)
            {
                try
                {
                    tmp.text = string.Format(_activeWarningMessageFormat, WorkItemDisplayName(item));
                }
                catch (System.FormatException)
                {
                    tmp.text = $"{WorkItemDisplayName(item)} is broken!";
                }

                tmp.ForceMeshUpdate();
            }

            float dur = warningSlideInDuration;
            if (dur <= 0f)
                entryRt.anchoredPosition = endPos;
            else
                state.SlideRoutine = StartCoroutine(SlideWarningEntryRoutine(entryRt, startPos, endPos, state));
        }
        else
        {
            TMP_Text tmp = entry.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null)
            {
                try
                {
                    tmp.text = string.Format(_activeWarningMessageFormat, WorkItemDisplayName(item));
                }
                catch (System.FormatException)
                {
                    tmp.text = $"{WorkItemDisplayName(item)} is broken!";
                }

                tmp.ForceMeshUpdate();
            }
        }
    }

    IEnumerator SlideWarningEntryRoutine(RectTransform rt, Vector2 from, Vector2 to, BrokenWarningState state)
    {
        float dur = Mathf.Max(0.0001f, warningSlideInDuration);
        bool useCurve = warningSlideInEase != null && warningSlideInEase.length > 0;
        float t = 0f;
        while (t < dur)
        {
            t += Time.deltaTime;
            float u = Mathf.Clamp01(t / dur);
            if (useCurve)
                u = warningSlideInEase.Evaluate(u);
            rt.anchoredPosition = Vector2.LerpUnclamped(from, to, u);
            yield return null;
        }

        rt.anchoredPosition = to;
        state.SlideRoutine = null;
    }

    void ClearBrokenWarningForItem(WorkItem item)
    {
        if (item == null || !_brokenWarnings.TryGetValue(item, out BrokenWarningState state))
            return;

        if (state.DelayRoutine != null)
        {
            StopCoroutine(state.DelayRoutine);
            state.DelayRoutine = null;
        }

        if (state.SlideRoutine != null)
        {
            StopCoroutine(state.SlideRoutine);
            state.SlideRoutine = null;
        }

        if (state.Instance != null)
        {
            Destroy(state.Instance);
            state.Instance = null;
        }

        state.ReservedSlot = null;
        _brokenWarnings.Remove(item);
    }

    void ClearAllBrokenWarnings()
    {
        if (_brokenWarnings.Count == 0) return;
        var keys = new List<WorkItem>(_brokenWarnings.Keys);
        for (int i = 0; i < keys.Count; i++)
            ClearBrokenWarningForItem(keys[i]);
    }

    public void GameOver(string reason)
    {
        if (isGameOver || IsVictory) return;
        isGameOver = true;

        if (_bossLoopCoroutine != null)
        {
            StopCoroutine(_bossLoopCoroutine);
            _bossLoopCoroutine = null;
        }
        if (_tutorialCoroutine != null)
        {
            StopCoroutine(_tutorialCoroutine);
            _tutorialCoroutine = null;
        }

        BossWarning = false;
        BossIsHere = false;

        FreezeFailures = false;

        bool bossCaused = reason != null && (reason.Contains("Boss") || reason.Contains("Work is too low"));
        if (bossCaused)
            OnGameOverBossCaused?.Invoke();

        if (screenTint != null)
            screenTint.SetTarget(0f, 0.1f);

        ClearAllBrokenWarnings();

        if (ui != null)
            ui.ShowGameOver(surviveTime, work, reason, TotalPerformanceScore);

        Time.timeScale = 0f;
    }

    public void Restart()
    {
        Time.timeScale = 1f;
        SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
    }

    public void Quit()
    {
        Time.timeScale = 1f;
        Application.Quit();
    }
}
