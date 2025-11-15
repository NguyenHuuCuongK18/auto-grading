using System.Text;

public class Helper
{
    private Helper() { }

    /// <summary>
    /// Student must use this method to format the object before printing it to the console
    /// </summary>
    /// <returns>string contain all item attribute</returns>
    public static string Stringify<T>(T item)
    {
        return Stringify(item!, new HashSet<object>());
    }

    private static string Stringify(object item, HashSet<object> visited, int indent = 0)
    {
        if (item == null)
            return "null";

        var type = item.GetType();

        bool isSimple =
            type.IsPrimitive ||
            item is string ||
            item is decimal ||
            item is DateTime ||
            item is Guid;

        if (!isSimple)
        {
            if (visited.Contains(item))
                return "<circular reference>";
            visited.Add(item);
        }

        if (type.IsPrimitive || item is string || item is decimal || item is DateTime || item is Guid)
        {
            if (item is string)
                return $"{item}";
            if (item is DateTime dt)
                return $"{dt.ToString("MM/dd/yyyy hh:mm:ss tt")}";
            return $"{item}";
        }

        if (typeof(System.Collections.IDictionary).IsAssignableFrom(type))
        {
            var dict = (System.Collections.IDictionary)item;
            var builder = new StringBuilder();

            foreach (var key in dict.Keys)
            {
                builder.Append($"{key}: {Stringify(dict[key], visited, indent + 2)}");
            }

            return builder.ToString();
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
        {
            var builder = new StringBuilder();

            foreach (var element in (System.Collections.IEnumerable)item)
            {
                builder.Append(Stringify(element, visited, indent + 2));
            }

            return builder.ToString();
        }

        var props = type.GetProperties();
        var indentStr = new string(' ', indent);
        var innerIndentStr = new string(' ', indent + 2);

        var sb = new StringBuilder();

        bool firstProp = true;
        foreach (var prop in props)
        {
            if (!firstProp) sb.Append(", ");
            sb.AppendLine();
            sb.Append(innerIndentStr);
            sb.Append($"{prop.Name}: ");

            var value = prop.GetValue(item);
            sb.Append(Stringify(value, visited, indent + 2));

            firstProp = false;
        }

        if (props.Length > 0)
        {
            sb.Append(indentStr);
        }

        return sb.ToString();
    }
}