using System.Globalization;

namespace DSQQ.Models;

// 活跃度字段的单位换算。
//
// 为什么单独放一个类型：界面用「秒」表示时间类字段（填 300 而不是 300000），
// 而桥接端点收的是毫秒。这层换算若放在视图的私有方法里就没法单元测试，
// 而单位换算是这类界面最容易出错、又最难靠肉眼发现的地方。
//
// 约定：
//   · 秒 → 毫秒时若不能整除（例如 1.5 秒），显示保留小数，避免悄悄丢精度；
//   · 毫秒 → 秒时同理（1500 毫秒显示为 "1.5"）；
//   · 非时间类字段原样传递，不做任何加工。
public static class ActivityUnitConverter
{
    // 秒单位字段的换算系数。
    private const int MillisecondsPerSecond = 1000;

    // 桥接值（毫秒）→ 界面显示文本。
    // 参数 raw：桥接返回的原始值文本。
    // 参数 unit：界面单位，"s" 表示需要换算。
    // 返回：可直接放进输入框的文本。
    public static string ToDisplay(string raw, string unit)
    {
        if (unit != "s" || !int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
        {
            return raw;
        }

        return milliseconds % MillisecondsPerSecond == 0
            ? (milliseconds / MillisecondsPerSecond).ToString()
            : (milliseconds / (double)MillisecondsPerSecond).ToString("0.###", CultureInfo.InvariantCulture);
    }

    // 界面输入 → 桥接值（毫秒）。
    // 参数 text：输入框里的文本。
    // 参数 unit：界面单位。
    // 参数 value：换算后的桥接值。
    // 返回：文本是否为合法数字。
    public static bool TryToBridgeValue(string text, string unit, out int value)
    {
        value = 0;

        // 固定用不变区域：否则小数点符号随系统区域变化 ——
        // 中文区域把逗号当千位分隔符、德法区域更把它当小数点，
        // 同一个 "1,5" 在不同机器上会解析成完全不同的值。
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            return false;
        }

        if (unit == "s")
        {
            var milliseconds = number * MillisecondsPerSecond;

            // 超出 int 范围时判为非法，而不是溢出成负数。
            if (milliseconds is > int.MaxValue or < int.MinValue)
            {
                return false;
            }

            value = (int)Math.Round(milliseconds);
            return true;
        }

        if (number is > int.MaxValue or < int.MinValue)
        {
            return false;
        }

        value = (int)Math.Round(number);
        return true;
    }

    // 单位的中文说明，用于行尾提示。
    public static string UnitLabel(string unit) => unit switch
    {
        "s" => "秒",
        "ms" => "毫秒",
        _ => "次",
    };
}
