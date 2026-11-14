using UnityEditor;
using UnityEngine;

/// <summary>
/// Wind2D inspector: buttons to play a gust and flip the wind (in or out of play mode) at the top, then the
/// usual fields.
/// </summary>
[CustomEditor(typeof(Wind2D))]
public class Wind2DEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var wind = (Wind2D)target;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Test", EditorStyles.boldLabel);

            if (!wind.isActiveAndEnabled)
            {
                EditorGUILayout.HelpBox("Put the prefab in a scene (e.g. Game, at the board's centre) to preview it.",
                                        MessageType.Info);
            }

            using (new EditorGUI.DisabledScope(!wind.isActiveAndEnabled))
            {
                EditorGUILayout.LabelField("Blowing", (wind.Direction > 0 ? "→ right" : "← left") +
                                                      (wind.IsGusting ? "   (playing...)" : ""));

                if (GUILayout.Button(new GUIContent("Gust + Flip (as in game)",
                        "Exactly what a gust does in a match: gust this way, die down, turn around, " +
                        "then breeze the other way."), GUILayout.Height(30)))
                    wind.TestGust();

                using (new EditorGUILayout.HorizontalScope())
                {
                    if (wind.IsHoldingGust)
                    {
                        if (GUILayout.Button(new GUIContent("Stop Gust",
                                "Drop straight back to the breeze."), GUILayout.Height(24)))
                            wind.StopGust();
                    }
                    else if (GUILayout.Button(new GUIContent("Gust Only",
                                 "Just the gust: builds up and keeps blowing at full strength until stopped."),
                                 GUILayout.Height(24)))
                    {
                        wind.TestGustOnly();
                    }
                    if (GUILayout.Button(new GUIContent("Flip Only",
                            "Just the turn-around: the breeze dies down, flips and picks back up."), GUILayout.Height(24)))
                        wind.FlipDirection();
                }
            }
        }

        EditorGUILayout.Space();
        DrawDefaultInspector();

        // Keep the gust label and direction current while a test gust plays.
        if (wind.IsGusting) Repaint();
    }
}
