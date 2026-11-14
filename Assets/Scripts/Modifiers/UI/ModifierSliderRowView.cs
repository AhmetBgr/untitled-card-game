using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// A row for an <see cref="IntModifierSO"/> or <see cref="FloatModifierSO"/>: a slider over the
/// modifier's range. Whole numbers for the int kind, so the handle snaps.
/// </summary>
public class ModifierSliderRowView : ModifierRowView
{
    [SerializeField] private Slider slider;

    public override void Bind(GameModifierSO modifier)
    {
        if (slider != null)
        {
            slider.onValueChanged.RemoveListener(OnSlid);

            switch (modifier)
            {
                case IntModifierSO i:
                    slider.wholeNumbers = true;
                    slider.minValue = i.min;
                    slider.maxValue = i.max;
                    break;
                case WeatherModifierSO w:
                    slider.wholeNumbers = true;
                    slider.minValue = w.Min;
                    slider.maxValue = w.Max;
                    break;
                case FloatModifierSO f:
                    slider.wholeNumbers = false;
                    slider.minValue = f.min;
                    slider.maxValue = f.max;
                    break;
            }

            slider.onValueChanged.AddListener(OnSlid);
        }

        base.Bind(modifier);
    }

    public override void Refresh()
    {
        base.Refresh();
        if (slider != null && Modifier != null) slider.SetValueWithoutNotify(MatchModifiers.GetValue(Modifier));
    }

    private void OnSlid(float value) => MatchModifiers.SetValue(Modifier, value);
}
