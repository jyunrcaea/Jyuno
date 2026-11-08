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
    public static Type ElementType(IList list)
    {
        Type type = list.GetType();
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
    public static IList create_like(IList origin , List<object?> items)
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

    //(컬렉션) (인덱스) (값)
    public static void Set(IList list , object? index , object? value)
    {
        int i = index_of(list , index);
        Type element = ElementType(list);
        if (!TryFit(value , element , out object? fitted))
            throw new JyunoException($"'{value ?? "null"}'은/는 이 컬렉션의 원소 타입({element.Name})에 넣을수 없습니다.");
        if (list.IsReadOnly)
            throw new JyunoException("읽기 전용 컬렉션의 원소는 바꿀수 없습니다.");
        list[i] = fitted;
    }

    //─────────── array.* / list.* 명령어 ───────────

    //첫 인자가 배열/리스트인지 확인
    public static IList Target(object?[] args , string name)
    {
        if (args.Length is 0)
            throw new JyunoException($"{name}: 배열/리스트가 필요합니다.");
        return args[0] switch {
            IList list => list,
            null => throw JyunoCommands.null_exception,
            _ => throw new JyunoException($"{name}: '{Values.Show(args[0])}'은/는 배열/리스트가 아닙니다.")
        };
    }

    //원소를 바꿀수 있는지 (sort, reverse, shuffle)
    static void writable(IList list , string name)
    {
        if (list.IsReadOnly)
            throw new JyunoException($"{name}: 읽기 전용 컬렉션은 바꿀수 없습니다.");
    }

    //길이를 바꿀수 있는지 (list.push, list.pop 등)
    static void resizable(IList list , string name)
    {
        if (list.IsReadOnly || list.IsFixedSize)
            throw new JyunoException($"{name}: 길이를 바꿀수 없는 컬렉션(배열 등)입니다. list로 만든 리스트를 사용하세요.");
    }

    static object?[] items_of(IList list)
    {
        object?[] items = new object?[list.Count];
        list.CopyTo(items , 0);
        return items;
    }

    static void write_back(IList list , object?[] items)
    {
        for (int i = 0 ; i < items.Length ; i++)
            list[i] = items[i];
    }

    //정수 인자 (범위: min ~ max)
    static int integer_argument(object? value , string what , long min , long max , string name)
    {
        if (!Values.TryGetInteger(value , out long i))
            throw new JyunoException($"{name}: {what}은/는 정수여야 합니다. ('{Values.Show(value)}')");
        if (i < min || i > max)
            throw new JyunoException($"{name}: {what} {i}은/는 범위({min} ~ {max})를 벗어났습니다.");
        return (int)i;
    }

    //less/greater와 같은 기준으로 비교 (NaN은 가장 앞, 비교할수 없는 값이 섞여있다면 오류)
    static int compare(object? a , object? b)
    {
        if (Values.Compare(a , b) is int order)
            return order;
        //Compare가 null을 돌려주는 경우는 NaN이 섞여있을 때뿐
        bool nan_a = Values.TryGetReal(a , out double ra) && double.IsNaN(ra);
        bool nan_b = Values.TryGetReal(b , out double rb) && double.IsNaN(rb);
        return nan_a == nan_b ? 0 : nan_a ? -1 : 1;
    }

    //안정 정렬 (병합 정렬, 같은 값의 순서를 유지, sign이 -1이라면 내림차순)
    //Array.Sort는 비교 함수의 예외를 InvalidOperationException으로 감싸므로 직접 구현
    static void merge_sort(object?[] items , object?[] buffer , int start , int end , int sign)
    {
        if (end - start < 2)
            return;
        int middle = (start + end) / 2;
        merge_sort(items , buffer , start , middle , sign);
        merge_sort(items , buffer , middle , end , sign);
        //이미 정렬되어 있다면 합칠 필요가 없음
        if (sign * compare(items[middle - 1] , items[middle]) <= 0)
            return;
        Array.Copy(items , start , buffer , start , end - start);
        int left = start, right = middle, at = start;
        while (left < middle && right < end)
            items[at++] = sign * compare(buffer[right] , buffer[left]) < 0 ? buffer[right++] : buffer[left++];
        while (left < middle)
            items[at++] = buffer[left++];
        while (right < end)
            items[at++] = buffer[right++];
    }

    //array.sort / list.sort: 오름차순으로 정렬 (원래 컬렉션을 바꾸고 그대로 반환, 두번째 인자가 참이라면 내림차순)
    public static IList Sort(object?[] args , string name)
    {
        IList list = Target(args , name);
        writable(list , name);
        object?[] items = items_of(list);
        int sign = args.Length > 1 && Compiler.Parser.IsTrue(args[1]) ? -1 : 1;
        merge_sort(items , new object?[items.Length] , 0 , items.Length , sign);
        write_back(list , items);
        return list;
    }

    //array.reverse / list.reverse: 순서를 뒤집음 (원래 컬렉션을 바꾸고 그대로 반환)
    public static IList Reverse(object?[] args , string name)
    {
        IList list = Target(args , name);
        writable(list , name);
        object?[] items = items_of(list);
        Array.Reverse(items);
        write_back(list , items);
        return list;
    }

    //array.shuffle / list.shuffle: 무작위로 섞음 (Fisher-Yates, 원래 컬렉션을 바꾸고 그대로 반환)
    public static IList Shuffle(object?[] args , string name)
    {
        IList list = Target(args , name);
        writable(list , name);
        object?[] items = items_of(list);
        for (int i = items.Length - 1 ; i > 0 ; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (items[i] , items[j]) = (items[j] , items[i]);
        }
        write_back(list , items);
        return list;
    }

    //array.indexof / list.indexof: equal과 같은 기준으로 처음 같은 원소의 인덱스 (없다면 -1)
    public static long IndexOf(object?[] args , string name)
    {
        IList list = Target(args , name);
        if (args.Length < 2)
            throw new JyunoException($"{name}: 찾을 값이 필요합니다.");
        for (int i = 0 ; i < list.Count ; i++)
        {
            if (Values.Same(list[i] , args[1]))
                return i;
        }
        return -1;
    }

    //array.contains / list.contains
    public static bool Contains(object?[] args , string name) => IndexOf(args , name) >= 0;

    //array.slice / list.slice: [start, end) 범위의 원소로 만든 새 컬렉션 (end를 생략하면 끝까지)
    public static IList Slice(object?[] args , string name)
    {
        IList list = Target(args , name);
        if (args.Length < 2)
            throw new JyunoException($"{name}: 시작 인덱스가 필요합니다.");
        int start = integer_argument(args[1] , "시작 인덱스" , 0 , list.Count , name);
        int end = args.Length > 2 ? integer_argument(args[2] , "끝 인덱스" , start , list.Count , name) : list.Count;
        List<object?> items = new(end - start);
        for (int i = start ; i < end ; i++)
            items.Add(list[i]);
        return create_like(list , items);
    }

    //array.join / list.join: 원소를 문자열로 바꿔서 구분자(생략하면 빈 문자열)로 이어붙임
    public static string Join(object?[] args , string name)
    {
        IList list = Target(args , name);
        string separator = args.Length > 1 ? Convert.ToString(args[1] , CultureInfo.InvariantCulture) ?? string.Empty : string.Empty;
        string[] texts = new string[list.Count];
        for (int i = 0 ; i < texts.Length ; i++)
            texts[i] = Convert.ToString(list[i] , CultureInfo.InvariantCulture) ?? string.Empty;
        return string.Join(separator , texts);
    }

    //array.new / list.new: 길이가 length이고 모든 원소가 value(생략하면 null)인 배열/리스트
    public static IList New(object?[] args , bool array , string name)
    {
        if (args.Length is 0)
            throw new JyunoException($"{name}: 길이가 필요합니다.");
        int length = integer_argument(args[0] , "길이" , 0 , Array.MaxLength , name);
        object? value = args.Length > 1 ? args[1] : null;
        object?[] items = new object?[length];
        Array.Fill(items , value);
        return array ? NewArray(items) : NewList(items);
    }

    //array.copy / list.copy: 같은 종류, 같은 원소 타입의 새 컬렉션 (얕은 복사)
    public static IList Copy(object?[] args , string name)
    {
        IList list = Target(args , name);
        return create_like(list , new List<object?>(items_of(list)));
    }

    //array.lastindexof / list.lastindexof: equal과 같은 기준으로 마지막으로 같은 원소의 인덱스 (없다면 -1)
    public static long LastIndexOf(object?[] args , string name)
    {
        IList list = Target(args , name);
        if (args.Length < 2)
            throw new JyunoException($"{name}: 찾을 값이 필요합니다.");
        for (int i = list.Count - 1 ; i >= 0 ; i--)
        {
            if (Values.Same(list[i] , args[1]))
                return i;
        }
        return -1;
    }

    //array.count / list.count: equal과 같은 기준으로 같은 원소의 개수
    public static long Count(object?[] args , string name)
    {
        IList list = Target(args , name);
        if (args.Length < 2)
            throw new JyunoException($"{name}: 셀 값이 필요합니다.");
        long count = 0;
        foreach (object? item in list)
        {
            if (Values.Same(item , args[1]))
                count++;
        }
        return count;
    }

    //array.distinct / list.distinct: equal과 같은 기준으로 중복을 제거한 새 컬렉션 (처음 나온 원소를 남김)
    public static IList Distinct(object?[] args , string name)
    {
        IList list = Target(args , name);
        HashSet<object?> seen = new(SameComparer.Instance);
        List<object?> items = new();
        foreach (object? item in list)
        {
            if (seen.Add(item))
                items.Add(item);
        }
        return create_like(list , items);
    }

    //array.binary_search / list.binary_search: 오름차순으로 정렬된 컬렉션에서 값의 인덱스 (sort와 같은 기준)
    //같은 값이 여러개라면 가장 앞의 인덱스, 없다면 들어갈 위치의 비트 반전(C#의 Array.BinarySearch처럼 음수, not으로 위치를 구할수 있음)
    public static long BinarySearch(object?[] args , string name)
    {
        IList list = Target(args , name);
        if (args.Length < 2)
            throw new JyunoException($"{name}: 찾을 값이 필요합니다.");
        int low = 0, high = list.Count;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (compare(list[middle] , args[1]) < 0)
                low = middle + 1;
            else
                high = middle;
        }
        if (low < list.Count && compare(list[low] , args[1]) == 0)
            return low;
        return ~(long)low;
    }

    //array.fill / list.fill: 모든 원소를 값으로 바꿈 (원래 컬렉션을 바꾸고 그대로 반환)
    public static IList Fill(object?[] args , string name)
    {
        IList list = Target(args , name);
        writable(list , name);
        object? value = fit(list , args.Length > 1 ? args[1] : null , name);
        for (int i = 0 ; i < list.Count ; i++)
            list[i] = value;
        return list;
    }

    //array.min, array.max: sort와 같은 기준으로 가장 작은/큰 원소 (같다면 앞의 원소)
    public static object? Extreme(object?[] args , int sign , string name)
    {
        IList list = Target(args , name);
        if (list.Count is 0)
            throw new JyunoException($"{name}: 빈 컬렉션입니다.");
        object? best = list[0];
        for (int i = 1 ; i < list.Count ; i++)
        {
            object? item = list[i];
            if (sign * compare(item , best) > 0)
                best = item;
        }
        return best;
    }

    //array.sum / list.sum: 모든 원소(숫자)를 add와 같이 더함 (빈 컬렉션이라면 0)
    public static object? Sum(object?[] args , string name)
    {
        IList list = Target(args , name);
        object? sum = 0L;
        foreach (object? item in list)
        {
            if (!Values.TryGetReal(item , out _))
                throw new JyunoException($"{name}: '{Values.Show(item)}'은/는 숫자가 아닙니다.");
            sum = Values.Add(sum , item);
        }
        return sum;
    }

    //array.range / list.range: 정수 수열 (count → 0 ~ count-1, start end → start ~ end-1, start end step → step 간격)
    public static IList Range(object?[] args , bool array , string name)
    {
        if (args.Length is 0)
            throw new JyunoException($"{name}: 범위가 필요합니다.");
        long start = 0, end, step = 1;
        long integer(int i , string what)
        {
            if (!Values.TryGetInteger(args[i] , out long value))
                throw new JyunoException($"{name}: {what}은/는 정수여야 합니다. ('{Values.Show(args[i])}')");
            return value;
        }
        if (args.Length is 1)
        {
            end = integer(0 , "끝");
        }
        else
        {
            start = integer(0 , "시작");
            end = integer(1 , "끝");
            if (args.Length > 2)
                step = integer(2 , "간격");
        }
        if (step is 0)
            throw new JyunoException($"{name}: 간격은 0이 될수 없습니다.");
        //원소 개수 (long 범위 전체에서 넘치지 않도록 ulong으로 계산)
        ulong count = 0;
        if (step > 0 && start < end)
            count = unchecked((ulong)(end - start) - 1) / (ulong)step + 1;
        else if (step < 0 && start > end)
            count = unchecked((ulong)(start - end) - 1) / unchecked((ulong)(-step)) + 1;
        if (count > (ulong)Array.MaxLength)
            throw new JyunoException($"{name}: 원소가 너무 많습니다. ({count}개)");
        long[] items = new long[count];
        for (int i = 0 ; i < items.Length ; i++)
            items[i] = unchecked(start + i * step);
        return array ? items : new List<long>(items);
    }

    //원소 타입에 맞게 변환 (list.push, list.insert)
    static object? fit(IList list , object? value , string name)
    {
        Type element = ElementType(list);
        if (!TryFit(value , element , out object? fitted))
            throw new JyunoException($"{name}: '{Values.Show(value)}'은/는 이 컬렉션의 원소 타입({element.Name})에 넣을수 없습니다.");
        return fitted;
    }

    //list.push: 리스트 끝에 값들을 추가 (리스트를 그대로 반환)
    public static IList Push(object?[] args , string name)
    {
        IList list = Target(args , name);
        resizable(list , name);
        //하나라도 넣을수 없다면 아무것도 추가하지 않음
        object?[] values = new object?[args.Length - 1];
        for (int i = 1 ; i < args.Length ; i++)
            values[i - 1] = fit(list , args[i] , name);
        foreach (object? value in values)
            list.Add(value);
        return list;
    }

    //list.pop: 마지막 원소를 제거하고 반환
    public static object? Pop(object?[] args , string name)
    {
        IList list = Target(args , name);
        resizable(list , name);
        if (list.Count is 0)
            throw new JyunoException($"{name}: 빈 리스트에서 원소를 꺼낼수 없습니다.");
        object? last = list[list.Count - 1];
        list.RemoveAt(list.Count - 1);
        return last;
    }

    //list.insert: index 위치에 값을 끼워넣음 (리스트를 그대로 반환)
    public static IList Insert(object?[] args , string name)
    {
        IList list = Target(args , name);
        resizable(list , name);
        if (args.Length < 3)
            throw new JyunoException($"{name}: 리스트, 인덱스, 값이 필요합니다.");
        int index = integer_argument(args[1] , "인덱스" , 0 , list.Count , name);
        list.Insert(index , fit(list , args[2] , name));
        return list;
    }

    //list.removeat: index 위치의 원소를 제거하고 반환
    public static object? RemoveAt(object?[] args , string name)
    {
        IList list = Target(args , name);
        resizable(list , name);
        if (args.Length < 2)
            throw new JyunoException($"{name}: 제거할 인덱스가 필요합니다.");
        if (list.Count is 0)
            throw new JyunoException($"{name}: 빈 리스트에서 원소를 제거할수 없습니다.");
        int index = integer_argument(args[1] , "인덱스" , 0 , list.Count - 1 , name);
        object? removed = list[index];
        list.RemoveAt(index);
        return removed;
    }

    //list.remove: equal과 같은 기준으로 처음 같은 원소 하나를 제거 (제거했다면 true)
    public static bool Remove(object?[] args , string name)
    {
        IList list = Target(args , name);
        resizable(list , name);
        long index = IndexOf(args , name);
        if (index < 0)
            return false;
        list.RemoveAt((int)index);
        return true;
    }

    //list.clear: 모든 원소를 제거 (리스트를 그대로 반환)
    public static IList Clear(object?[] args , string name)
    {
        IList list = Target(args , name);
        resizable(list , name);
        list.Clear();
        return list;
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
