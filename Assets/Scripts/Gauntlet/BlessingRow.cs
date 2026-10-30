using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// One enemy blessing on an opponent briefing (Assets/Prefabs/UI/Draft/BlessingRow): its name and what it
/// does, one row per blessing. A blessing rolled more than once (Extra Passive) gets one row, with a count.
/// The versus intro uses it for the player's milestone rewards too (<see cref="Show(string, string, int)"/>).
/// </summary>
public class BlessingRow : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI text;
    [SerializeField] private string nameColor = "#FFD36B";

    public void Show(GauntletBlessing blessing, int stacks) =>
        Show(GauntletRun.BlessingName(blessing), GauntletRun.BlessingDescription(blessing), stacks);

    /// <summary>Any named bonus, worded like a blessing: "Name x2: what it does".</summary>
    public void Show(string name, string description, int stacks)
    {
        if (text == null) text = GetComponentInChildren<TextMeshProUGUI>(true);
        if (text == null) return;

        text.text = $"<color={nameColor}>{name}{(stacks > 1 ? $" x{stacks}" : "")}</color>: {description}";
    }

    /// <summary>
    /// Fills <paramref name="list"/> with one row per blessing, cloning <paramref name="prefab"/> into
    /// <paramref name="rows"/> as more are needed and hiding the spares. Returns how many rows are showing.
    /// </summary>
    public static int Fill(RectTransform list, BlessingRow prefab, List<BlessingRow> rows,
        IReadOnlyList<GauntletBlessing> blessings)
    {
        var listed = new List<GauntletBlessing>();
        if (blessings != null)
            foreach (var blessing in blessings)
                if (!listed.Contains(blessing)) listed.Add(blessing);

        if (list == null || prefab == null) return listed.Count;

        while (rows.Count < listed.Count)
            rows.Add(Instantiate(prefab, list));

        for (int i = 0; i < rows.Count; i++)
        {
            bool used = i < listed.Count;
            rows[i].gameObject.SetActive(used);
            if (!used) continue;

            int stacks = 0;
            foreach (var b in blessings) if (b == listed[i]) stacks++;
            rows[i].Show(listed[i], stacks);
        }
        return listed.Count;
    }
}
