using RiMCP.Bridge;
using RiMCP.Command;
using RiMCP.Read;
using UnityEngine;

namespace RiMCP
{
    internal sealed class RiMCPUnityDriver : MonoBehaviour
    {
        private static RiMCPUnityDriver instance;

        public static void EnsureStarted()
        {
            if (instance != null)
            {
                return;
            }

            BridgeRuntime.SetApplicationFocused(Application.isFocused);
            BridgeRuntime.SetRunInBackground(Application.runInBackground);
            GameObject gameObject = new GameObject("RiMCP Driver");
            DontDestroyOnLoad(gameObject);
            instance = gameObject.AddComponent<RiMCPUnityDriver>();
            // OnGUI only notes input; no layout pass is needed.
            instance.useGUILayout = false;
        }

        private void Update()
        {
            AdvanceRunner.Update();
            OperationRunner.Update();
            Interactions.Update();
            // Before requests run, so a window the player just opened is known as theirs.
            // Known limit: a window a mod opens from Update on a key press is scanned before OnGUI
            // sees that key, so it counts as the game's. Reading UnityEngine.Input here would need
            // the InputLegacyModule reference, which the build does not take today.
            WindowOrigins.Update();
            HistoryWatch.Update();
            SetupController.Update();
            FoodHistory.Update();
            BridgeRuntime.ProcessMainThreadQueue();
            // Read after handling requests: set_game_settings may have just turned this off, and an
            // unfocused game stops calling Update before the next frame.
            BridgeRuntime.SetRunInBackground(Application.runInBackground);
        }

        // The player's clicks and key presses, so windows they open are told from the game's.
        private void OnGUI()
        {
            WindowOrigins.NoteInput(Event.current);
        }

        private void OnApplicationFocus(bool focused)
        {
            BridgeRuntime.SetApplicationFocused(focused);
        }

        private void OnApplicationQuit()
        {
            BridgeRuntime.Stop();
        }

        private void OnDestroy()
        {
            if (instance == this)
            {
                instance = null;
            }
        }
    }
}
