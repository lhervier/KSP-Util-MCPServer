using System;
using System.Collections.Generic;
using UnityEngine;

namespace com.github.lhervier.ksp.util.mcpserver
{
    /// <summary>
    /// Holds the MCP server for the whole session, across scene changes: listening from the start of KSP, or
    /// only once the player turns it on from its toolbar button; turned off the same way.
    /// </summary>
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class KSPUtilMCPServer : MonoBehaviour
    {
        private const float SwitchMessageDuration = 4f;

        private Settings _settings;
        private McpProtocol _protocol;
        private McpHttpServer _server;
        private ServerButton _button;
        private bool _subscribed;

        private void Awake()
        {
            Log.SetMainThread();
            DontDestroyOnLoad(gameObject);
            MainThread.SetHost(this);

            List<Tool> tools = new List<Tool>();
            tools.AddRange(StateTools.All());
            tools.AddRange(GameTools.All());
            tools.AddRange(SceneTools.All());
            tools.AddRange(VesselTools.All());
            tools.AddRange(SpaceCenterTools.All());
            tools.AddRange(TimeTools.All());
            tools.AddRange(ViewTools.All());
            tools.AddRange(MouseTools.All());
            tools.AddRange(StationTools.All());
            tools.AddRange(FlightTools.All());
            tools.AddRange(PartMenuTools.All());
            tools.AddRange(PartViewTools.All());
            tools.AddRange(DriveTools.All());
            tools.AddRange(ReflectionTools.All());
            tools.AddRange(ExtensionTools.All());

            _settings = Settings.Read();
            ToolMessage.SetEnabled(_settings.ScreenMessages);
            _protocol = new McpProtocol(tools);
            _button = new ServerButton(() => SetListening(true), () => SetListening(false));
            if (_settings.ListenAtStart)
            {
                StartListening();
            }
            else
            {
                Log.Info("Not listening until turned on from the toolbar");
            }
        }

        private void Update()
        {
            Subscribe();
            Log.Flush();
            MainThread.Pump();
        }

        private void OnDestroy()
        {
            _server?.Stop();
            Log.Flush();
            if (_subscribed)
            {
                GameEvents.onFloatingOriginShift.Remove(OnOriginShift);
                GameEvents.onFlightReady.Remove(OnFlightReady);
                GameEvents.onGUIApplicationLauncherReady.Remove(_button.OnLauncherReady);
                GameEvents.onGUIApplicationLauncherDestroyed.Remove(_button.OnLauncherDestroyed);
            }
        }

        // Turns the server on or off, as the player asked, and tells them on screen how it ended.
        private void SetListening(bool on)
        {
            if (on == (_server != null))
            {
                _button.Show(on);
                return;
            }
            string text;
            if (on)
            {
                text = StartListening()
                    ? "KSP-Util-MCPServer on: listening on 127.0.0.1:" + _settings.Port
                    : "KSP-Util-MCPServer could not start: see KSP.log";
            }
            else
            {
                StopListening();
                text = "KSP-Util-MCPServer off";
            }
            ScreenMessages.PostScreenMessage(text, SwitchMessageDuration, ScreenMessageStyle.UPPER_CENTER);
        }

        // Opens the port; returns whether it could.
        private bool StartListening()
        {
            McpHttpServer server = new McpHttpServer(_settings.Port, _protocol);
            try
            {
                server.Start();
                _server = server;
            }
            catch (Exception e)
            {
                Log.Error("Could not start: " + e.Message);
                server.Stop();
            }
            _button.Show(_server != null);
            return _server != null;
        }

        // Closes the port and stops the tools still running, so that nothing drives the game any more.
        private void StopListening()
        {
            _server.Stop();
            _server = null;
            List<MainThread.Job> stopped = MainThread.Cancel(job => true, "the server was turned off");
            Log.Info("Turned off" + (stopped.Count > 0 ? ", stopping " + stopped.Count + " running tool(s)" : ""));
            _button.Show(false);
        }

        // The game events, subscribed once they exist: KSP creates them after the very first addons start.
        // Instance methods: EventData refuses a static handler.
        private void Subscribe()
        {
            if (_subscribed || GameEvents.onFloatingOriginShift == null || GameEvents.onFlightReady == null
                || GameEvents.onGUIApplicationLauncherReady == null || GameEvents.onGUIApplicationLauncherDestroyed == null)
            {
                return;
            }
            GameEvents.onFloatingOriginShift.Add(OnOriginShift);
            GameEvents.onFlightReady.Add(OnFlightReady);
            GameEvents.onGUIApplicationLauncherReady.Add(_button.OnLauncherReady);
            GameEvents.onGUIApplicationLauncherDestroyed.Add(_button.OnLauncherDestroyed);
            _subscribed = true;
            // The toolbar may already be up.
            _button.OnLauncherReady();
        }

        private void OnOriginShift(Vector3d offset, Vector3d nonFrame)
        {
            GameState.OnOriginShift(offset, nonFrame);
        }

        private void OnFlightReady()
        {
            GameState.OnFlightReady();
        }
    }
}
