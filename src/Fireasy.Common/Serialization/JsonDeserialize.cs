﻿// -----------------------------------------------------------------------
// <copyright company="Fireasy"
//      email="faib920@126.com"
//      qq="55570729">
//   (c) Copyright Fireasy. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using Fireasy.Common.Extensions;
using Fireasy.Common.Reflection;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Dynamic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Fireasy.Common.Serialization
{
    internal sealed class JsonDeserialize : DeserializeBase
    {
        private readonly JsonSerializeOption option;
        private readonly JsonSerializer serializer;
        private readonly JsonReader jsonReader;
        private static readonly MethodInfo MthToArray = typeof(Enumerable).GetMethod(nameof(Enumerable.ToArray), BindingFlags.Public | BindingFlags.Static);
        private readonly TypeConverterCache<JsonConverter> cacheConverter = new TypeConverterCache<JsonConverter>();

        internal JsonDeserialize(JsonSerializer serializer, JsonReader reader, JsonSerializeOption option)
            : base(option)
        {
            this.serializer = serializer;
            jsonReader = reader;
            this.option = option;
        }

        internal T Deserialize<T>()
        {
            return (T)Deserialize(typeof(T));
        }

        internal object Deserialize(Type type)
        {
            object value = null;
            if (WithSerializable(type, ref value))
            {
                return value;
            }

            if (WithConverter(type, ref value))
            {
                return value;
            }

            jsonReader.SkipWhiteSpaces();

            if (type == typeof(Type))
            {
                return DeserializeType();
            }

            if (typeof(IDynamicMetaObjectProvider).IsAssignableFrom(type) ||
                type == typeof(object))
            {
                return DeserializeIntelligently(type);
            }
            if (type == typeof(byte[]))
            {
                return DeserializeBytes();
            }
            if (type.GetNonNullableType() == typeof(TimeSpan))
            {
                return DeserializeTimeSpan(type.IsNullableType());
            }

            if (typeof(ArrayList).IsAssignableFrom(type))
            {
                return DeserializeSingleArray();
            }

            if (typeof(IDictionary).IsAssignableFrom(type) && type != typeof(string))
            {
                return DeserializeDictionary(type);
            }

            if (typeof(IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            {
                return DeserializeList(type);
            }

            if (type == typeof(DataSet))
            {
                return DeserializeDataSet();
            }

            if (type == typeof(DataTable))
            {
                return DeserializeDataTable();
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName.StartsWith("System.Tuple`", StringComparison.InvariantCulture))
            {
                return DeserializeTuple(type);
            }

            if (type.IsEnum)
            {
                return DeserializeEnum(type);
            }

            return DeserializeValue(type);
        }

        private bool WithSerializable(Type type, ref object value)
        {
            if (typeof(ITextSerializable).IsAssignableFrom(type))
            {
                var obj = type.New<ITextSerializable>();
                var rawValue = jsonReader.ReadRaw();
                if (rawValue != null)
                {
                    obj.Deserialize(serializer, rawValue);
                    value = obj;
                }

                return true;
            }

            return false;
        }

        private bool WithConverter(Type type, ref object value)
        {
            var converter = cacheConverter.GetReadableConverter(type, option);

            if (converter == null || !converter.CanRead)
            {
                return false;
            }

            value = converter.ReadJson(serializer, jsonReader, type);

            return true;
        }

        private object DeserializeValue(Type type)
        {
            if (jsonReader.IsNull())
            {
                if ((type.GetNonNullableType().IsValueType && !type.IsNullableType()))
                {
                    throw new SerializationException(SR.GetString(SRKind.JsonNullableType, type));
                }

                return null;
            }

            var stype = type.GetNonNullableType();
            var typeCode = Type.GetTypeCode(stype);
            if (typeCode == TypeCode.Object)
            {
                return ParseObject(type);
            }

            var value = jsonReader.ReadValue();
            if (type.IsNullableType() && (value == null || string.IsNullOrEmpty(value.ToString())))
            {
                return null;
            }

            switch (typeCode)
            {
                case TypeCode.DateTime:
                    CheckNullString(value, type);
                    return SerializerUtil.ParseDateTime(value.ToString(), option.Culture, option.DateTimeZoneHandling);
                case TypeCode.String:
                    return value == null ? null : DeserializeString(value.ToString());
                default:
                    CheckNullString(value, type);
                    try
                    {
                        return value.ToType(stype);
                    }
                    catch (Exception ex)
                    {
                        throw new SerializationException(SR.GetString(SRKind.DeserializeError, value, type), ex);
                    }
            }
        }

        private static void CheckNullString(object value, Type type)
        {
            if ((value == null || value.ToString().Length == 0) && !type.IsNullableType())
            {
                throw new SerializationException(SR.GetString(SRKind.JsonNullableType, type));
            }
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000")]
        private DataSet DeserializeDataSet()
        {
            var ds = new DataSet();
            jsonReader.SkipWhiteSpaces();
            jsonReader.AssertAndConsume(JsonTokens.StartObjectLiteralCharacter);
            while (true)
            {
                jsonReader.SkipWhiteSpaces();
                var name = jsonReader.ReadAsString();
                jsonReader.SkipWhiteSpaces();
                jsonReader.AssertAndConsume(JsonTokens.PairSeparator);
                jsonReader.SkipWhiteSpaces();
                var tb = DeserializeDataTable();
                tb.TableName = name;
                ds.Tables.Add(tb);
                jsonReader.SkipWhiteSpaces();
                if (jsonReader.AssertNextIsDelimiterOrSeparator(JsonTokens.EndObjectLiteralCharacter))
                {
                    break;
                }
            }

            return ds;
        }

        [SuppressMessage("Microsoft.Reliability", "CA2000")]
        private DataTable DeserializeDataTable()
        {
            var tb = new DataTable();
            jsonReader.SkipWhiteSpaces();
            jsonReader.AssertAndConsume(JsonTokens.StartArrayCharacter);
            while (true)
            {
                jsonReader.SkipWhiteSpaces();
                DeserializeDataRow(tb);
                jsonReader.SkipWhiteSpaces();
                if (jsonReader.AssertNextIsDelimiterOrSeparator(JsonTokens.EndArrayCharacter))
                {
                    break;
                }
            }

            return tb;
        }

        private void DeserializeDataRow(DataTable tb)
        {
            if (jsonReader.Peek() != JsonTokens.StartObjectLiteralCharacter)
            {
                return;
            }

            var noCols = tb.Columns.Count == 0;
            jsonReader.AssertAndConsume(JsonTokens.StartObjectLiteralCharacter);
            var row = tb.NewRow();
            while (true)
            {
                jsonReader.SkipWhiteSpaces();
                var name = DeserializeString(jsonReader.ReadKey());
                jsonReader.SkipWhiteSpaces();
                jsonReader.AssertAndConsume(JsonTokens.PairSeparator);
                jsonReader.SkipWhiteSpaces();
                var obj = ParseValue(jsonReader.ReadValue());
                jsonReader.SkipWhiteSpaces();
                if (noCols)
                {
                    tb.Columns.Add(name, obj != null ? obj.GetType() : typeof(object));
                }

                if (tb.Columns.Contains(name))
                {
                    row[name] = obj ?? DBNull.Value;
                }

                if (jsonReader.AssertNextIsDelimiterOrSeparator(JsonTokens.EndObjectLiteralCharacter))
                {
                    break;
                }
            }

            tb.Rows.Add(row);
        }

        private object ParseValue(object value)
        {
            if (value is string s)
            {
                if (Regex.IsMatch(s, @"Date\((\d+)\+(\d+)\)"))
                {
                    return DeserializeDateTime(s);
                }

                return DeserializeString(s);
            }

            return value;
        }

        private object DeserializeList(Type listType)
        {
            var isReadonly = listType.IsGenericType && listType.GetGenericTypeDefinition() == typeof(IReadOnlyCollection<>);

            CreateListContainer(listType, out Type elementType, out IList container);

            jsonReader.SkipWhiteSpaces();
            if (jsonReader.IsNull())
            {
                return null;
            }

            jsonReader.AssertAndConsume(JsonTokens.StartArrayCharacter);
            while (true)
            {
                if (jsonReader.Peek() == JsonTokens.EndArrayCharacter)
                {
                    jsonReader.Read();
                    break;
                }

                jsonReader.SkipWhiteSpaces();
                var value = DeserializeIntelligently(elementType);
                if (value != null && value.GetType() != elementType)
                {
                    value = value.ToType(elementType);
                }
                container.Add(value);
                jsonReader.SkipWhiteSpaces();

                if (jsonReader.AssertNextIsDelimiterOrSeparator(JsonTokens.EndArrayCharacter))
                {
                    break;
                }
            }

            if (listType.IsArray)
            {
                var invoker = ReflectionCache.GetInvoker(MthToArray.MakeGenericMethod(elementType));
                return invoker.Invoke(null, container);
            }

            if (isReadonly)
            {
                return listType.GetConstructor(BindingFlags.Instance | BindingFlags.Public, null, new[] { container.GetType() }, null).Invoke(new object[] { container });
            }

            return container;
        }

        private IDictionary DeserializeDictionary(Type dictType)
        {
            CreateDictionaryContainer(dictType, out Type[] keyValueTypes, out IDictionary container);

            jsonReader.SkipWhiteSpaces();
            jsonReader.AssertAndConsume(JsonTokens.StartObjectLiteralCharacter);
            while (true)
            {
                if (jsonReader.Peek() == JsonTokens.EndObjectLiteralCharacter)
                {
                    jsonReader.Read();
                    return container;
                }

                jsonReader.SkipWhiteSpaces();
                var key = Deserialize(keyValueTypes[0]);
                jsonReader.SkipWhiteSpaces();
                jsonReader.AssertAndConsume(JsonTokens.PairSeparator);
                jsonReader.SkipWhiteSpaces();
                container.Add(key, Deserialize(keyValueTypes[1]));
                jsonReader.SkipWhiteSpaces();

                if (jsonReader.AssertNextIsDelimiterOrSeparator(JsonTokens.EndObjectLiteralCharacter))
                {
                    break;
                }
            }

            return container;
        }

        private object DeserializeIntelligently(Type type)
        {
            if (jsonReader.IsNull())
            {
                return null;
            }

            if (jsonReader.IsNextCharacter(JsonTokens.StartArrayCharacter))
            {
                return DeserializeSingleArray();
            }
            else if (jsonReader.IsNextCharacter(JsonTokens.StartObjectLiteralCharacter))
            {
                if (typeof(object) == type)
                {
                    return DeserializeDynamicObject(type);
                }

                return Deserialize(type);
            }

            return jsonReader.ReadValue();
        }

        private object DeserializeDynamicObject(Type type)
        {
            var dynamicObject = type == typeof(object) ? new ExpandoObject() :
                type.New<IDictionary<string, object>>();

            jsonReader.AssertAndConsume(JsonTokens.StartObjectLiteralCharacter);

            while (true)
            {
                jsonReader.SkipWhiteSpaces();
                var name = jsonReader.ReadKey();
                jsonReader.SkipWhiteSpaces();
                jsonReader.AssertAndConsume(JsonTokens.PairSeparator);
                jsonReader.SkipWhiteSpaces();

                object value;
                if (jsonReader.IsNextCharacter(JsonTokens.StartArrayCharacter))
                {
                    value = DeserializeList(typeof(List<dynamic>));
                }
                else if (jsonReader.IsNextCharacter(JsonTokens.StartObjectLiteralCharacter))
                {
                    value = Deserialize<dynamic>();
                }
                else
                {
                    value = jsonReader.ReadValue();
                }

                dynamicObject.Add(name, value);
                jsonReader.SkipWhiteSpaces();
                if (jsonReader.AssertNextIsDelimiterOrSeparator(JsonTokens.EndObjectLiteralCharacter))
                {
                    break;
                }
            }

            return dynamicObject;
        }

        private object DeserializeTuple(Type type)
        {
            if (jsonReader.IsNull())
            {
                return null;
            }

            var genericTypes = type.GetGenericArguments();
            var arguments = new object[genericTypes.Length];

            jsonReader.AssertAndConsume(JsonTokens.StartObjectLiteralCharacter);

            while (true)
            {
                jsonReader.SkipWhiteSpaces();
                if (jsonReader.IsNextCharacter(JsonTokens.EndObjectLiteralCharacter))
                {
                    break;
                }

                var name = jsonReader.ReadKey();

                jsonReader.SkipWhiteSpaces();
                jsonReader.AssertAndConsume(JsonTokens.PairSeparator);
                jsonReader.SkipWhiteSpaces();

                var index = GetTupleItemIndex(name);
                if (index != -1)
                {
                    arguments[index] = Deserialize(genericTypes[index]);
                }

                jsonReader.SkipWhiteSpaces();
                if (jsonReader.AssertNextIsDelimiterOrSeparator(JsonTokens.EndObjectLiteralCharacter))
                {
                    break;
                }
            }

            return type.New(arguments);
        }

        private static int GetTupleItemIndex(string name)
        {
            var match = Regex.Match(name, @"Item(\d)");
            if (match.Success && match.Groups.Count > 0)
            {
                return match.Groups[1].Value.To<int>() - 1;
            }
            else
            {
                return -1;
            }
        }

        private object DeserializeEnum(Type enumType)
        {
            jsonReader.SkipWhiteSpaces();
            string evalue;
            if (jsonReader.IsNextCharacter('"'))
            {
                evalue = jsonReader.ReadAsString();
            }
            else
            {
                evalue = jsonReader.ReadAsInt32().ToString(option.Culture);
            }

            return Enum.Parse(enumType, evalue);
        }

        private byte[] DeserializeBytes()
        {
            var str = jsonReader.ReadAsString();
            if (string.IsNullOrEmpty(str))
            {
                return null;
            }

            return Convert.FromBase64String(str);
        }

        private TimeSpan? DeserializeTimeSpan(bool isNullable)
        {
            if (jsonReader.IsNull())
            {
                return isNullable ? (TimeSpan?)null : TimeSpan.Zero;
            }

            var str = jsonReader.ReadAsString();
            if (TimeSpan.TryParse(str, out TimeSpan result))
            {
                return result;
            }

            return null;
        }

        private object DeserializeSingleArray()
        {
            var array = new ArrayList();
            jsonReader.AssertAndConsume(JsonTokens.StartArrayCharacter);

            while (true)
            {
                jsonReader.SkipWhiteSpaces();
                var value = Deserialize(typeof(object));
                array.Add(value);
                jsonReader.SkipWhiteSpaces();

                if (jsonReader.AssertNextIsDelimiterOrSeparator(JsonTokens.EndArrayCharacter))
                {
                    break;
                }
            }

            return array;
        }

        private DateTime? DeserializeDateTime(string value)
        {
            if (value.Length == 0)
            {
                return null;
            }

            return SerializerUtil.ParseDateTime(value, null, option.DateTimeZoneHandling);
        }

        private static string DeserializeString(string value)
        {
            if (Regex.IsMatch(value, "(?<code>\\\\u[0-9a-fA-F]{4})", RegexOptions.IgnoreCase))
            {
                return value.DeUnicode();
            }

            return value;
        }

        private Type DeserializeType()
        {
            var value = jsonReader.ReadAsString();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return value.ParseType();
        }

        private object ParseObject(Type type)
        {
            return type.IsAnonymousType() ? ParseAnonymousObject(type) : ParseGeneralObject(type);
        }

        private object ParseGeneralObject(Type type)
        {
            if (jsonReader.IsNull())
            {
                return null;
            }


            jsonReader.AssertAndConsume(JsonTokens.StartObjectLiteralCharacter);

            var instance = CreateGeneralObject(type);
            var mappers = GetAccessorMetadataMappers(instance.GetType());
            var processor = instance as IDeserializeProcessor;
            processor?.PreDeserialize();

            while (true)
            {
                jsonReader.SkipWhiteSpaces();
                if (jsonReader.IsNextCharacter(JsonTokens.EndObjectLiteralCharacter))
                {
                    break;
                }

                var name = jsonReader.ReadKey();
                jsonReader.SkipWhiteSpaces();
                jsonReader.AssertAndConsume(JsonTokens.PairSeparator);
                jsonReader.SkipWhiteSpaces();

                if (!mappers.TryGetValue(name, out SerializerPropertyMetadata metadata))
                {
                    jsonReader.ReadValue();
                }
                else
                {
                    var value = Deserialize(metadata.PropertyInfo.PropertyType);
                    if (value != null)
                    {
                        if (processor == null || !processor.SetValue(name, value))
                        {
                            metadata.Setter?.Invoke(instance, value);
                        }
                    }
                }

                jsonReader.SkipWhiteSpaces();
                if (jsonReader.AssertNextIsDelimiterOrSeparator(JsonTokens.EndObjectLiteralCharacter))
                {
                    break;
                }
            }

            processor?.PostDeserialize();

            return instance;
        }

        private object ParseAnonymousObject(Type type)
        {
            jsonReader.AssertAndConsume(JsonTokens.StartObjectLiteralCharacter);
            var dic = new Dictionary<string, object>();

            var constructor = type.GetConstructors()[0];
            var conInvoker = ReflectionCache.GetInvoker(constructor);

            var mapper = GenerateParameterDictionary(constructor);
            var values = GenerateParameterValues(mapper);

            while (true)
            {
                jsonReader.SkipWhiteSpaces();
                var name = jsonReader.ReadKey();

                jsonReader.SkipWhiteSpaces();
                jsonReader.AssertAndConsume(JsonTokens.PairSeparator);
                jsonReader.SkipWhiteSpaces();

                var par = mapper.FirstOrDefault(s => s.Key == name);

                if (string.IsNullOrEmpty(par.Key))
                {
                    jsonReader.ReadRaw();
                }
                else
                {
                    values[name] = Deserialize(par.Value);
                }

                jsonReader.SkipWhiteSpaces();

                if (jsonReader.AssertNextIsDelimiterOrSeparator(JsonTokens.EndObjectLiteralCharacter))
                {
                    break;
                }
            }

            return conInvoker.Invoke(values.Values.ToArray());
        }

        private static Dictionary<string, Type> GenerateParameterDictionary(ConstructorInfo constructor)
        {
            var dic = new Dictionary<string, Type>();
            foreach (var par in constructor.GetParameters())
            {
                dic.Add(par.Name, par.ParameterType);
            }

            return dic;
        }

        private static Dictionary<string, object> GenerateParameterValues(Dictionary<string, Type> mapper)
        {
            var dic = new Dictionary<string, object>();
            foreach (var par in mapper)
            {
                dic.Add(par.Key, null);
            }

            return dic;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                jsonReader.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
