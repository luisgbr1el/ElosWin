using System;
using Microsoft.UI.Xaml.Data;

namespace ElosWin;

public class GlyphSelectorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        bool isTrue = value is bool b && b;
        string param = parameter?.ToString() ?? "";

        if (param == "Mute")
        {
            // Se mutado: Microfone Cortado (F781), senão: Microfone Normal (E720)
            return isTrue ? "\uF781" : "\uE720";
        }
        else if (param == "Deafen")
        {
            // Se ensurdecido: Mudo (E74F), senão: Som Alto (E767)
            return isTrue ? "\uE74F" : "\uE767";
        }

        return "\uE700";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        throw new NotImplementedException();
    }
}