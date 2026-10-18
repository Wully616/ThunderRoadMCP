using System;
using System.IO;
using Newtonsoft.Json;
using ThunderRoad;
using UnityEngine;

namespace ThunderRoadMcp.Bridge
{
    public sealed class ThunderRoadMcpThunderScript : ThunderScript
    {
        private BridgeServer server;
        private string configPath;

        public override void ScriptEnable()
        {
            base.ScriptEnable();
            try
            {
                configPath = Environment.GetEnvironmentVariable("THUNDERROAD_MCP_CONFIG");
                if (string.IsNullOrWhiteSpace(configPath))
                    configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThunderRoadMcp", "bridge.json");
                if (!File.Exists(configPath))
                {
                    Debug.Log("[ThunderRoadMcp] Bridge disabled: local opt-in config is missing.");
                    return;
                }
                if (new FileInfo(configPath).Length > 8192)
                    throw new InvalidOperationException("Bridge config exceeds 8 KiB.");

                BridgeConfig config = JsonConvert.DeserializeObject<BridgeConfig>(File.ReadAllText(configPath));
                if (config == null || !config.Enabled)
                {
                    Debug.Log("[ThunderRoadMcp] Bridge disabled by local config.");
                    return;
                }

                config.Validate();
                string payloadDirectory = FileManager.GetFullPath(FileManager.Type.JSONCatalog, FileManager.Source.Mods, Path.Combine("ThunderRoadMcp", "runtime"));
                CSharpEvaluator.Configure(payloadDirectory);
                BridgeLogs.StartCapture();
                McpRuntime.EnsureCreated();
                server = new BridgeServer(config);
                server.Start();
                Debug.Log("[ThunderRoadMcp] Loopback bridge listening on port " + config.Port + ".");
            }
            catch (Exception ex)
            {
                StopBridge();
                Debug.LogError("[ThunderRoadMcp] Bridge startup failed: " + ex);
            }
        }

        public override void ScriptUpdate()
        {
            base.ScriptUpdate();
            MainThreadQueue.Drain();
        }

        public override void ScriptDisable()
        {
            StopBridge();
            base.ScriptDisable();
        }

        public override void ScriptUnload()
        {
            StopBridge();
            base.ScriptUnload();
        }

        private void StopBridge()
        {
            if (server != null)
            {
                server.Dispose();
                server = null;
            }
            BridgeLogs.StopCapture();
            MainThreadQueue.CancelPending();
            McpRuntime.Shutdown();
        }
    }
}
