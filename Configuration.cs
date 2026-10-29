using System;
using Dalamud.Configuration;

namespace LatihasKTG;

[Serializable]
internal sealed class Configuration : IPluginConfiguration {
	public int Version { get; set; } = 1;

	public int StableMs = 150;
	public int IntervalMs = 750;
	public bool DisableLeaveDuty = true;
	public bool InfiniteMode = true;
	public bool AutoPurchase;

	internal void Save() => Plugin.pluginInterface.SavePluginConfig(this);
}
