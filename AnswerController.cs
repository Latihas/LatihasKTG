using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using static LatihasKTG.Plugin;

namespace LatihasKTG;

internal sealed class AnswerController {
	private enum Phase {
		Choosing,
		Filled,
		AwaitingClear,
		Cooldown
	}

	private Phase phase;
	private string session = "";
	private WordTarget? pending;
	private long since;
	private readonly Dictionary<WordTarget, int> attempts = new();
	private AnswerOptions options = new();
	internal bool Running { get; private set; }
	internal string Status { get; private set; } = "已停止";
	internal string LastWord { get; private set; } = "";
	internal int Submissions { get; private set; }

	internal bool Start(long now, AnswerOptions settings) {
		if (Running) {
			return true;
		}
		if (!GameUiPort.Read(out var initialSession, out _, out _, out var reason, out _) || string.IsNullOrEmpty(initialSession)) {
			Status = "不能启动：" + reason;
			return false;
		}
		options = settings.Clamp();
		session = initialSession;
		attempts.Clear();
		pending = null;
		since = now;
		phase = Phase.Choosing;
		Running = true;
		Submissions = 0;
		LastWord = "";
		Status = "等待可见目标";
		return true;
	}

	internal void Stop(string reason = "已停止") {
		Running = false;
		pending = null;
		Status = reason;
	}

	internal void Tick(long now) {
		if (!Running) {
			return;
		}
		if (!GameUiPort.Read(out var frameSession, out var inputText, out var frameTargets, out var reason, out var retryable)) {
			if (retryable) {
				Status = "等待输入控件恢复：" + reason;
				return;
			}
			Stop(reason);
			return;
		}
		if (frameSession != session) {
			Stop("游戏界面已重建，请重新启动");
			return;
		}
		var live = frameTargets.ToHashSet();
		if (options.IntervalMs == 0) {
			TickSingleWithoutInterval(now, live);
			return;
		}
		for (var actions = 0; actions < 64; actions++) {
		if (phase == Phase.AwaitingClear) {
			if (inputText.Length != 0) return;
			phase = Phase.Cooldown;
			since = now;
			Status = "提交回调已处理，等待下一次输入";
		}
		if (phase == Phase.Filled) {
			if (pending == null) {
				Stop("内部目标状态无效");
				return;
			}
			if (!live.Contains(pending)) {
				GameUiPort.ClearOwnedInput(session, pending.Text);
				pending = null;
				phase = Phase.Choosing;
				Status = "目标已消失，重新选择";
				return;
			}
			if (now - since >= 0) {
				if (!GameUiPort.Submit(session, pending, out var submitError, out var submitRetryable)) {
					if (submitRetryable) {
						Status = "等待输入控件恢复：" + submitError;
						return;
					}
					Stop(submitError);
					return;
				}
				attempts[pending] = attempts.GetValueOrDefault(pending) + 1;
				Submissions++;
				LastWord = pending.Text;
				phase = Phase.AwaitingClear;
				since = now;
				Status = "已调用提交，等待输入清空";
			}
			if (phase == Phase.AwaitingClear) {
				if (!GameUiPort.Read(out _, out inputText, out frameTargets, out _, out _)) return;
				live = frameTargets.ToHashSet();
				continue;
			}
			return;
		}
		if (phase == Phase.Cooldown) {
			if (now - since < options.IntervalMs) {
				return;
			}
			phase = Phase.Choosing;
			pending = null;
		}
		var next = (from t in live
			orderby attempts.GetValueOrDefault(t), t.Text.EnumerateRunes().Count()
			select t).ThenBy(t => t.Key, StringComparer.Ordinal).FirstOrDefault();
		if (next == null) {
			pending = null;
			Status = "等待可见目标";
		} else if (next != pending) {
			pending = next;
			since = now;
			Status = "等待词条稳定：" + next.Text;
		} else if (now - since >= options.StableMs) {
			if (!GameUiPort.Fill(next, out var fillError, out var fillRetryable)) {
				if (fillRetryable) {
					Status = "等待输入控件恢复：" + fillError;
					return;
				}
				Stop(fillError);
				return;
			}
			phase = Phase.Filled;
			since = now;
			Status = "已填入：" + next.Text;
		}
		if (options.IntervalMs > 0 || phase != Phase.Choosing) return;
		}
	}

	private void TickSingleWithoutInterval(long now, HashSet<WordTarget> live) {
		var target = live
			.OrderBy(t => attempts.GetValueOrDefault(t))
			.ThenBy(t => t.Text.EnumerateRunes().Count())
			.ThenBy(t => t.Key, StringComparer.Ordinal)
			.FirstOrDefault();
		if (target == null) {
			pending = null;
			Status = "等待新的可见目标";
			return;
		}
		if (!GameUiPort.Fill(target, out var fillError, out var fillRetryable)) {
			if (fillRetryable) {
				Status = "等待输入控件恢复：" + fillError;
				return;
			}
			Stop(fillError);
			return;
		}
		if (!GameUiPort.Submit(session, target, out var submitError, out var submitRetryable)) {
			if (submitRetryable) {
				Status = "等待输入控件恢复：" + submitError;
				return;
			}
			Stop(submitError);
			return;
		}
		attempts[target] = attempts.GetValueOrDefault(target) + 1;
		Submissions++;
		LastWord = target.Text;
		pending = null;
		phase = Phase.Choosing;
		since = now;
		GameUiPort.ClearOwnedInput(session, target.Text);
		pending = null;
		Status = "已提交：" + target.Text;
	}

	internal static bool IsValid(WordTarget target) {
		return !string.IsNullOrWhiteSpace(target.Text)
		       && Encoding.UTF8.GetByteCount(target.Text) <= 512
		       && target.Text.All(c => !char.IsControl(c));
	}
}
