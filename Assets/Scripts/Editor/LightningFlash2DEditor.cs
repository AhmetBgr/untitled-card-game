using UnityEditor;
using UnityEngine;

/// <summary>
/// LightningFlash2D inspector: a button to trigger a lightning strike (in or out of play mode) at the top,
/// then the usual fields.
/// </summary>
[CustomEditor(typeof(LightningFlash2D))]
public class LightningFlash2DEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var flash = (LightningFlash2D)target;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Test", EditorStyles.boldLabel);

            if (!flash.isActiveAndEnabled)
            {
                EditorGUILayout.HelpBox("Put the prefab in a scene (e.g. Game) to preview it.", MessageType.Info);
            }

            if (flash.ThunderSound == null)
                EditorGUILayout.HelpBox("No Thunder Sound assigned: the strike is silent.", MessageType.Warning);
            else if (!flash.ThunderSound.HasAudibleClip)
                EditorGUILayout.HelpBox($"'{flash.ThunderSound.name}' has no audio clip yet: the strike is silent. " +
                                        "Drop a clip into its Clips list.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(!flash.isActiveAndEnabled))
            {
                if (GUILayout.Button(new GUIContent(flash.IsFlashing ? "Strike Lightning   (playing...)" : "Strike Lightning",
                        "Bolt, flash and delayed thunder, visual only (no damage). In a match it hits the " +
                        "highest-health minion(s); otherwise the middle of the screen."),
                        GUILayout.Height(30)))
                    StrikeTest(flash);
            }
        }

        EditorGUILayout.Space();
        DrawDefaultInspector();

        if (flash.IsFlashing) Repaint();
    }

    private static void StrikeTest(LightningFlash2D flash)
    {
        var system = Application.isPlaying ? WeatherSystem.Current : null;
        if (system != null)
        {
            // Same targets as in a match: every minion tied for the most health.
            var targets = new System.Collections.Generic.List<Vector3>();
            int best = int.MinValue;
            foreach (var minion in system.AllMinions())
            {
                if (minion.modal.health > best) { best = minion.modal.health; targets.Clear(); }
                if (minion.modal.health == best) targets.Add(minion.transform.position);
            }
            if (targets.Count > 0) { flash.Strike(targets); return; }
        }
        flash.Strike();
    }
}
