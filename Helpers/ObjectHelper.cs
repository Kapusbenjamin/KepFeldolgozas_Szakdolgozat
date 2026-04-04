namespace .Helpers
{
    public static class ObjectHelper
    {
        public static T Clone<T>(this T source, params PropertyValue[] overrides)
        {
            return Clone<T, T>(source, overrides);
        }

        public static G Clone<T, G>(this T source, params PropertyValue[] overrides)
        {
            G clone = Activator.CreateInstance<G>();

            if (source == null)
            {
                return clone;
            }

            foreach (var property in typeof(G).GetProperties().Where(c => c.CanWrite && c.Name != "Id"))
            {
                //check if property is virtual
                var method = property.GetMethod ?? property.SetMethod;
                if(method != null && method.IsVirtual && !method.IsFinal)
                {
                    continue;
                }

                var sourceProp = typeof(T).GetProperty(property.Name);
                if(sourceProp == null || !sourceProp.CanRead)
                {
                    continue;
                }

                property.SetValue(clone, sourceProp.GetValue(source));
            }

            foreach (var kvp in overrides)
            {
                var property = typeof(G).GetProperty(kvp.Name);
                if (property != null && property.CanWrite)
                {
                    property.SetValue(clone, kvp.Value);
                }
            }

            return clone;
        }

        public class PropertyValue
        {
            public PropertyValue(string name, object value)
            {
                this.Name = name;
                this.Value = value;
            }

            public string Name { get; private set; }
            public object Value { get; private set; }
        }
    }
}