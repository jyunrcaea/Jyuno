using System.Collections;
using System.Globalization;

namespace Jyuno.Language;

//Jyuno의 배열(T[])/리스트(List<T>)를 다루기 위한 도구
internal static class Collections
{
    //Jyuno에서 컬렉션으로 취급하는 값 (배열, 리스트 등 인덱스로 접근할수 있는 값)
    public static bool IsCollection(object? value) => value is IList;

    //원소의 타입: 모든 원소가 null이 아니고 같은 기본 타입(정수, 실수, 문자열, 논리형)이면 그 타입, 아니라면 object
    static Type infer_element_type(IReadOnlyList<object?> items)
    {
        if (items.Count is 0 || items[0] is null)
            return typeof(object);
        Type type = items[0]!.GetType();
        if (type != typeof(long) && type != typeof(double) && type != typeof(string) && type != typeof(bool))
            return typeof(object);
        for (int i = 1 ; i < items.Count ; i++)
        {
            if (items[i]?.GetType() != type)
                return typeof(object);
        }
        return type;
    }

    //array 명령어: 원소 타입에 맞는 배열 (AOT 환경에서도 동작하도록 타입을 직접 지정)
    public static Array NewArray(IReadOnlyList<object?> items)
    {
        Type type = infer_element_type(items);
        if (type == typeof(long)) return items.Select(x => (long)x!).ToArray();
        if (type == typeof(double)) return items.Select(x => (double)x!).ToArray();
        if (type == typeof(string)) return items.Select(x => (string)x!).ToArray();
        if (type == typeof(bool)) return items.Select(x => (bool)x!).ToArray();
        return items.ToArray();
    }

    //list 명령어: 원소 타입에 맞는 리스트
    public static IList NewList(IReadOnlyList<object?> items)
    {
        Type type = infer_element_type(items);
        if (type == typeof(long)) return items.Select(x => (long)x!).ToList();
        if (type == typeof(double)) return items.Select(x => (double)x!).ToList();
        if (type == typeof(string)) return items.Select(x => (string)x!).ToList();
        if (type == typeof(bool)) return items.Select(x => (bool)x!).ToList();
        return items.ToList();
    }

    //컬렉션의 원소 타입 (알수 없다면 object)
    public static Type ElementType(IList list) => element_types.GetOrAdd(list.GetType() , find_element_type);
    //리스트 값을 바꿀 때마다 인터페이스 목록을 훑지 않도록 타입별로 기억
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type , Type> element_types = new();
    static Type find_element_type(Type type)
    {
        if (type.IsArray)
            return type.GetElementType()!;
        foreach (Type i in type.GetInterfaces())
        {
            if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IList<>))
                return i.GetGenericArguments()[0];
        }
        return typeof(object);
    }

    //값을 원소 타입에 넣을수 있는 형태로 변환 (정수 → 실수 같은 숫자 변환은 허용)
    public static bool TryFit(object? value , Type type , out object? result)
    {
        result = value;
        if (value is null)
            return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
        if (type.IsInstanceOfType(value))
            return true;
        //정수 → 정수 타입 (범위를 넘으면 넣을수 없음), 숫자 → 실수 타입
        bool integer_type = Type.GetTypeCode(type) is TypeCode.Int64 or TypeCode.Int32 or TypeCode.Int16 or TypeCode.SByte
            or TypeCode.UInt64 or TypeCode.UInt32 or TypeCode.UInt16 or TypeCode.Byte;
        bool real_type = Type.GetTypeCode(type) is TypeCode.Double or TypeCode.Single or TypeCode.Decimal;
        if ((integer_type && Values.TryGetInteger(value , out _)) || (real_type && Values.TryGetReal(value , out _)))
        {
            try
            {
                result = Convert.ChangeType(value , type , CultureInfo.InvariantCulture);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }
        return false;
    }

    //결과 컬렉션 만들기: 가능하다면 기존 컬렉션과 같은 타입, 넣을수 없는 원소가 있다면 원소 타입을 새로 정함
    static IList create_like(IList origin , List<object?> items)
    {
        Type element = ElementType(origin);
        object?[] fitted = new object?[items.Count];
        bool fit = true;
        for (int i = 0 ; i < items.Count && fit ; i++)
            fit = TryFit(items[i] , element , out fitted[i]);
        if (fit)
        {
            if (origin is Array)
            {
                Array array = Array.CreateInstance(element , fitted.Length);
                for (int i = 0 ; i < fitted.Length ; i++)
                    array.SetValue(fitted[i] , i);
                return array;
            }
            //같은 타입의 리스트를 만들수 있다면 (기본 생성자가 있는 쓰기 가능한 리스트)
            if (!origin.IsReadOnly && !origin.IsFixedSize && origin.GetType().GetConstructor(Type.EmptyTypes) is not null
                && Activator.CreateInstance(origin.GetType()) is IList list)
            {
                foreach (object? item in fitted)
                    list.Add(item);
                return list;
            }
        }
        return origin is Array ? NewArray(items) : NewList(items);
    }

    //add 명령어: 첫 컬렉션 뒤에 나머지 인자를 이어붙임 (컬렉션은 한번만 펼침)
    public static IList Add(IList first , object?[] args , int start)
    {
        List<object?> items = new(first.Count);
        foreach (object? item in first)
            items.Add(item);
        for (int i = start ; i < args.Length ; i++)
        {
            if (args[i] is IList other)
            {
                foreach (object? item in other)
                    items.Add(item);
            }
            else
            {
                items.Add(args[i]);
            }
        }
        return create_like(first , items);
    }

    //sub 명령어: 차집합 (first에서 remove에 있는 원소와 같은 원소를 모두 제거, 컬렉션이 아니라면 그 값 하나를 제거)
    public static IList Sub(IList first , object? remove)
    {
        HashSet<object?> set = new(SameComparer.Instance);
        if (remove is IList other)
        {
            foreach (object? item in other)
                set.Add(item);
        }
        else
        {
            set.Add(remove);
        }
        List<object?> items = new(first.Count);
        foreach (object? item in first)
        {
            if (!set.Contains(item))
                items.Add(item);
        }
        return create_like(first , items);
    }

    //인덱스 확인 (정수이고 범위 안이어야 함)
    static int index_of(IList list , object? index)
    {
        if (!Values.TryGetInteger(index , out long i))
            throw new JyunoException($"인덱스는 정수여야 합니다. ('{index ?? "null"}')");
        if (i < 0 || i >= list.Count)
            throw new JyunoException($"인덱스 {i}은/는 범위를 벗어났습니다. (길이: {list.Count})");
        return (int)i;
    }

    //(컬렉션) (인덱스)
    public static object? Get(IList list , object? index) => list[index_of(list , index)];

    //[프로토타입] 정수 인덱스를 박싱하지 않고 (StaticJit, Get/Set과 같은 동작)
    internal static object? GetAt(IList list , long i)
    {
        if (i < 0 || i >= list.Count)
            throw new JyunoException($"인덱스 {i}은/는 범위를 벗어났습니다. (길이: {list.Count})");
        return list[(int)i];
    }
    internal static void SetAt(IList list , long i , object? value)
    {
        if (list is List<object?> objects)
        {
            if (i < 0 || i >= list.Count)
                throw new JyunoException($"인덱스 {i}은/는 범위를 벗어났습니다. (길이: {list.Count})");
            objects[(int)i] = value;
            return;
        }
        Set(list , i , value);
    }

    //(컬렉션) (인덱스) (값)
    public static void Set(IList list , object? index , object? value)
    {
        int i = index_of(list , index);
        //스크립트가 만든 리스트(List<object?>)는 어떤 값이든 넣을수 있으므로 원소 타입을 찾지 않음
        if (list is List<object?> objects)
        {
            objects[i] = value;
            return;
        }
        Type element = ElementType(list);
        if (!TryFit(value , element , out object? fitted))
            throw new JyunoException($"'{value ?? "null"}'은/는 이 컬렉션의 원소 타입({element.Name})에 넣을수 없습니다.");
        if (list.IsReadOnly)
            throw new JyunoException("읽기 전용 컬렉션의 원소는 바꿀수 없습니다.");
        list[i] = fitted;
    }

    //equal 명령어와 같은 기준으로 비교 (1과 1.0은 같음)
    sealed class SameComparer : IEqualityComparer<object?>
    {
        public static readonly SameComparer Instance = new();
        public new bool Equals(object? a , object? b) => Values.Same(a , b);
        public int GetHashCode(object? value)
        {
            if (value is null)
                return 0;
            //숫자는 타입이 달라도 값이 같다면 같은 해시가 나오도록 실수로 변환
            if (Values.TryGetReal(value , out double real))
                return real.GetHashCode();
            return value.GetHashCode();
        }
    }
}
