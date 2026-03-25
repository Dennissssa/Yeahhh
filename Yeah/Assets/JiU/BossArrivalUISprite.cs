using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace JiU
{
    /// <summary>
    /// Boss 预警：将 Image 物体 SetActive(true)，可选换「靠近」图。
    /// Boss 刚到：换第一张图；等待可编辑时间后再换第二张图。
    /// Boss 离开：换回默认 Sprite，再 SetActive(false)。
    /// 依赖 GameManager 的 OnBossWarningStarted / OnBossArrived / OnBossLeft。
    /// </summary>
    public class BossArrivalUISprite : MonoBehaviour
    {
        [Header("UI Image")]
        [Tooltip("要更换 Sprite 的 Image 组件（其 GameObject 会在预警时显示、Boss 离开后隐藏）")]
        public Image targetImage;

        [Header("预警（靠近）")]
        [Tooltip("预警开始时设为 true；若只想显示空底图可不填下方 Sprite")]
        public bool activateOnBossWarning = true;

        [Tooltip("可选：预警阶段显示的 Sprite（不填则只 Active，不改图）")]
        public Sprite spriteDuringApproach;

        [Header("Boss 刚到 → 延迟后")]
        [Tooltip("Boss 到达瞬间切换的 Sprite")]
        public Sprite spriteWhenBossHere;

        [Min(0f)]
        [Tooltip("从第一张切到第二张前等待的秒数（真实时间）")]
        public float delaySecondsBeforeSecondSprite = 0.5f;

        [Tooltip("延迟结束后切换的 Sprite；不填则保持第一张")]
        public Sprite spriteAfterDelayWhenBossHere;

        [Header("Boss 离开")]
        [Tooltip("离开后恢复的 Sprite；不填则用 Start 时记录的初始 Sprite")]
        public Sprite spriteWhenBossGone;

        Sprite _initialSprite;
        Coroutine _secondSpriteRoutine;

        void Start()
        {
            if (targetImage != null)
                _initialSprite = targetImage.sprite;

            if (GameManager.Instance == null) return;

            GameManager.Instance.OnBossWarningStarted.AddListener(OnBossWarningStarted);
            GameManager.Instance.OnBossArrived.AddListener(OnBossArrived);
            GameManager.Instance.OnBossLeft.AddListener(OnBossLeft);
        }

        void OnDestroy()
        {
            StopSecondSpriteRoutine();
            if (GameManager.Instance == null) return;
            GameManager.Instance.OnBossWarningStarted.RemoveListener(OnBossWarningStarted);
            GameManager.Instance.OnBossArrived.RemoveListener(OnBossArrived);
            GameManager.Instance.OnBossLeft.RemoveListener(OnBossLeft);
        }

        void OnBossWarningStarted()
        {
            if (targetImage == null) return;

            if (activateOnBossWarning)
                targetImage.gameObject.SetActive(true);

            if (spriteDuringApproach != null)
                targetImage.sprite = spriteDuringApproach;
        }

        void OnBossArrived()
        {
            if (targetImage == null) return;

            StopSecondSpriteRoutine();

            if (spriteWhenBossHere != null)
                targetImage.sprite = spriteWhenBossHere;

            if (spriteAfterDelayWhenBossHere != null)
                _secondSpriteRoutine = StartCoroutine(SecondSpriteAfterDelayRoutine());
        }

        IEnumerator SecondSpriteAfterDelayRoutine()
        {
            if (delaySecondsBeforeSecondSprite > 0f)
                yield return new WaitForSecondsRealtime(delaySecondsBeforeSecondSprite);

            if (targetImage != null && spriteAfterDelayWhenBossHere != null)
                targetImage.sprite = spriteAfterDelayWhenBossHere;

            _secondSpriteRoutine = null;
        }

        void StopSecondSpriteRoutine()
        {
            if (_secondSpriteRoutine != null)
            {
                StopCoroutine(_secondSpriteRoutine);
                _secondSpriteRoutine = null;
            }
        }

        void OnBossLeft()
        {
            StopSecondSpriteRoutine();
            if (targetImage == null) return;

            targetImage.sprite = spriteWhenBossGone != null ? spriteWhenBossGone : _initialSprite;
            targetImage.gameObject.SetActive(false);
        }

        /// <summary>兼容旧事件绑定：等同 Boss 到达第一张图逻辑（不启动延迟协程，仅供 Inspector 手动调用）</summary>
        public void SetBossSprite()
        {
            OnBossArrived();
        }

        /// <summary>兼容旧事件绑定：等同 Boss 离开</summary>
        public void SetNormalSprite()
        {
            OnBossLeft();
        }
    }
}
