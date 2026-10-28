using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using static LatihasKTG.Plugin;

namespace LatihasKTG;

internal sealed class MainWindow() : Window("LatihasMomo") {
	private bool delaysInitialized;
	private int operationDelayMs;
	private int intervalMs;

	internal void TryStart() {
		ClampDelayInputs();
		Start(operationDelayMs, intervalMs);
	}

	public override void Draw() {
		if (!delaysInitialized) {
			operationDelayMs = Options.FillDelayMs;
			intervalMs = Options.IntervalMs;
			delaysInitialized = true;
		}
		ImGui.TextUnformatted(Status);
		ImGui.BeginDisabled(Running);
		if (ImGui.Button("开始", new Vector2(100f, 0f))) {
			TryStart();
		}
		ImGui.EndDisabled();
		ImGui.SameLine();
		if (ImGui.Button("停止", new Vector2(100f, 0f))) {
			Stop();
		}
		ImGui.TextUnformatted($"提交调用：{Submissions}    最近词条：{LastWord}");

		if (ImGui.Checkbox("禁用LeaveDuty", ref disableLeaveDuty)) {
			if (disableLeaveDuty) Instance.ExecuteCommandHook.Enable();
			else Instance.ExecuteCommandHook.Disable();
		}
		ImGui.SameLine();
		if (ImGui.Button("强制LeaveDuty")) {
			if (LastLeaveDutyParam != null) {
				var p = LastLeaveDutyParam.Value;
				Instance.ExecuteCommandHook.Original(819, p.Item1, p.Item2, p.Item3, p.Item4);
			}
			LastLeaveDutyParam = null;
		}
		ImGui.Checkbox("全自动(需要先到柜台前)", ref infiniteMode);
		ImGui.SameLine();
		if (ImGui.Button("立刻排本")&&clientState.TerritoryType==1360) {
			disableLeaveDuty = true;
			Instance.ExecuteCommandHook.Enable();
			RequestRace();
		}

		ImGui.SetNextItemWidth(160f);
		ImGui.InputInt("操作延迟（ms，填入 → 提交）", ref operationDelayMs);
		ImGui.SetNextItemWidth(160f);
		ImGui.InputInt("答题间隔（ms）", ref intervalMs);
		if (ImGui.Button("保存延迟")) {
			ClampDelayInputs();
			SetOptions(Options with {
				FillDelayMs = operationDelayMs,
				IntervalMs = intervalMs
			});
		}
		ImGui.SameLine();
		ImGui.TextUnformatted($"已保存：操作 {Options.FillDelayMs} ms / 间隔 {Options.IntervalMs} ms");
		ImGui.Separator();
		if (!PreviewReady) {
			ImGui.TextWrapped($"当前不可输入：{PreviewReason}");
		} else {
			ImGui.TextUnformatted($"输入内容：{PreviewInput}");
			ImGui.TextWrapped($"可见目标：{string.Join(" | ", PreviewTargets.Select(t => t.Text))}");
		}
		var stable = Options.StableMs;
		var maxAttempts = Options.MaxAttemptsPerWord;
		if (ImGui.InputInt("词条稳定时间（毫秒）", ref stable)) {
			SetOptions(Options with {
				StableMs = stable
			});
		}
		if (ImGui.InputInt("同一可见词条提交上限（1–100）", ref maxAttempts)) {
			SetOptions(Options with {
				MaxAttemptsPerWord = maxAttempts
			});
		}
	}

	private void ClampDelayInputs() {
		operationDelayMs = Math.Clamp(operationDelayMs, 0, 60000);
		intervalMs = Math.Clamp(intervalMs, 0, 60000);
	}
}
