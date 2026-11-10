using Jyuno.Language;
using System.Collections;

namespace Jyuno;

//구조체 (1.5.0부터)
//struct (이름) ~ endstruct 사이는 생성자이며, 구조체를 호출하면 새 영역에서 생성자를 실행해서 구조체 값을 만듦
//생성자에서 만든 변수(param 포함)는 필드, 생성자에서 만든 함수는 구조체 함수가 됨 (구조체 함수의 바깥 영역은 구조체 값의 영역)
//구조체 값을 변수에 넣으면 깊은 복사를 해서 넣고, 같은 영역에 멤버 이름(변수.필드, 변수.함수)을 선언함
//(이미 같은 이름이 있다면 덮어씀, 구조체 값 안의 구조체는 변수.필드.필드처럼 이어서 선언)
public partial class Interpreter
{
    //struct로 만든 구조체 (호출하면 구조체 값을 만드는 함수)
    sealed class JyunoStructType : FunctionInterface
    {
        public JyunoStructType(Interpreter interpreter , string name , int line , int end , Scope scope , bool has_inner_struct)
        {
            this.interpreter = interpreter;
            Name = name;
            Line = line;
            End = end;
            Scope = scope;
            HasInnerStruct = has_inner_struct;
        }
        readonly Interpreter interpreter;
        public readonly string Name;
        public readonly int Line, End;
        //구조체를 만든 영역 (생성자와 구조체 함수에서 읽을수 있는 바깥 변수)
        public readonly Scope Scope;
        //struct와 endstruct 사이에 다른 struct가 있는지 (그 구조체의 값은 이 구조체 값의 영역을 바깥 영역으로 가짐)
        public readonly bool HasInnerStruct;
        public JyunoStructType Rebind(Scope scope) => new(interpreter , Name , Line , End , scope , HasInnerStruct);
        //같은 struct 줄에서 만든 구조체인지
        public bool SameDefinition(JyunoStructType other) => Line == other.Line && interpreter == other.interpreter;
        public object? Call(object?[] args) => interpreter.construct(this , args);
        public object? Execute(params object?[] args)
        {
            escape(args);
            object? result = Call(args);
            escape(result);
            return result;
        }
    }

    //구조체 값
    sealed class StructInstance : StructValue
    {
        public StructInstance(JyunoStructType type , Scope scope)
        {
            Type = type;
            Scope = scope;
            scope.Instance = this;
        }
        public readonly JyunoStructType Type;
        //필드와 구조체 함수가 있는 영역
        public readonly Scope Scope;
        //생성자에서 만든 이름 (만든 순서, 멤버 이름은 제외)
        public string[] Members = Array.Empty<string>();
        //이 구조체 값을 담은 변수 (멤버 이름이 선언된 영역과 이름)
        public Scope? OwnerScope;
        public string? OwnerName;
        //OwnerScope에 선언한 멤버 이름과 그 이름에 넣은 것 (지울때 사용)
        public List<(string Name, object? Holder)>? Declared;
        //[실험] 탈출 분석: 방금 만들어져서(변수 읽기의 복사본, 생성자의 결과) 아무 곳에도 담기지 않은 값인지
        //이런 값을 변수에 넣을 때는 다시 복사하지 않고 그대로 넣음 (소유권 이전)
        //C# 프로그램에 넘어가거나 배열/리스트에 담기면 false가 됨 (escape)
        public bool Fresh;

        public override string TypeName => Type.Name;
        public override IReadOnlyList<string> FieldNames => Members.Where(o => Scope.Variables.TryGetValue(o , out object? holder) && holder is VariableInterface).ToArray();
        public override bool TryGetField(string name , out object? value)
        {
            if (Members.Contains(name) && Scope.Variables.TryGetValue(name , out object? holder) && holder is VariableInterface vi)
            {
                value = vi.Get();
                return true;
            }
            value = null;
            return false;
        }

        //같은 구조체(같은 struct 줄)이고 필드의 값이 모두 같다면 같은 값 (배열/리스트 필드는 원소끼리 비교)
        public override bool Equals(object? obj) => obj is StructInstance other && Same(this , false , other , false);

        //copy_a, copy_b: 그 쪽의 값을 복사본으로 보고 비교할지
        //([실험] 탈출 분석이 변수의 구조체 값을 복사하지 않고 넘긴 경우: 복사본끼리 비교하던 원래 결과와 같아야 함)
        //같은 객체끼리는 필드를 비교하지 않고 같다고 보는데(NaN이 있다면 필드 비교와 결과가 다름), 복사본은 원래 값과 같은 객체일수 없으므로 필드를 비교함
        public static bool Same(StructInstance a , bool copy_a , StructInstance b , bool copy_b)
        {
            if (!a.Type.SameDefinition(b.Type))
                return false;
            if (ReferenceEquals(a , b) && !copy_a && !copy_b)
                return true;
            //필드 이름(FieldNames)이 모두 같은지 먼저 확인한 다음 값을 비교
            int i = 0, j = 0;
            while (true)
            {
                bool more = a.next_field(ref i , out string? name , out _);
                if (b.next_field(ref j , out string? other , out _) != more || name != other)
                    return false;
                if (!more)
                    break;
            }
            i = j = 0;
            while (a.next_field(ref i , out _ , out object? x) && b.next_field(ref j , out _ , out object? y))
            {
                if (!same(x , copy_a , y , copy_b))
                    return false;
            }
            return true;
        }
        //Members의 index번째부터 다음 필드(구조체 함수가 아닌 멤버)를 찾음
        bool next_field(ref int index , out string? name , out object? value)
        {
            while (index < Members.Length)
            {
                string member = Members[index++];
                if (Scope.Variables.TryGetValue(member , out object? holder) && holder is VariableInterface variable)
                {
                    name = member;
                    value = variable.Get();
                    return true;
                }
            }
            name = null;
            value = null;
            return false;
        }
        static bool same(object? a , bool copy_a , object? b , bool copy_b)
        {
            if (a is IList x && b is IList y && a is not string && b is not string)
            {
                //복사할 때 따로 만들어지는 배열/리스트(copy_value)만 복사본
                bool cloned_a = copy_a && is_cloned(x), cloned_b = copy_b && is_cloned(y);
                if (ReferenceEquals(x , y) && !cloned_a && !cloned_b)
                    return true;
                if (x.Count != y.Count)
                    return false;
                //원소도 복사되는 컬렉션일 때만 원소가 복사본
                bool deep_a = cloned_a && needs_copy(element_type(x)), deep_b = cloned_b && needs_copy(element_type(y));
                for (int i = 0 ; i < x.Count ; i++)
                {
                    if (!same(x[i] , deep_a , y[i] , deep_b))
                        return false;
                }
                return true;
            }
            if ((copy_a || copy_b) && a is StructInstance s && b is StructInstance t)
                return Same(s , copy_a , t , copy_b);
            return Values.Same(a , b);
        }
        public override int GetHashCode() => Type.Line;
    }

    //구조체를 호출: 새 영역에서 생성자(struct와 endstruct 사이)를 실행하고 구조체 값을 만듦
    object? construct(JyunoStructType type , object?[] args)
    {
        Scope scope = new(type.Scope);
        StructInstance instance = new(type , scope);
        //생성자의 return은 생성자를 끝내기만 함 (반환한 값은 무시)
        call_function(type.Line , type.End , scope , args);
        instance.Members = scope.Variables.Keys.Where(o => scope.Aliases is null || !scope.Aliases.ContainsKey(o)).ToArray();
        //복사본과 똑같은 모양일 때만 방금 만들어진 값으로 봄 (아니라면 변수에 넣을 때 원래대로 복사)
        instance.Fresh = EscapeAnalysis && copy_equivalent(instance);
        return instance;
    }

    //[실험] 탈출 분석: 생성자가 만든 구조체 값이 그 복사본(copy_struct)과 구별할수 없는 모양인지
    //복사하면 달라지는 경우: 바깥의 배열/리스트를 필드에 그대로 담았거나(param, 바깥 변수),
    //생성자에서 구조체 필드의 멤버 이름(필드.필드)을 지우거나 다른 것으로 덮어쓴 경우 (복사본은 멤버 이름을 모두 다시 선언함)
    //구조체 안에서 정의한 구조체가 있는 경우 (그 구조체의 값이 생성자 도중에 바깥으로 나갔다면, 그 값은 복사본이 아닌 이 값의 영역을 보고 있음)
    static bool copy_equivalent(StructInstance instance)
    {
        if (instance.Type.HasInnerStruct)
            return false;
        Scope scope = instance.Scope;
        int aliases = 0;
        foreach (string member in instance.Members)
        {
            if (!scope.Variables.TryGetValue(member , out object? holder))
                continue;
            //구조체 필드의 멤버 이름이 된 이름은 아래에서 구조체 필드와 함께 확인
            if (scope.Aliases is not null && scope.Aliases.ContainsKey(member))
                continue;
            switch (holder)
            {
                case JyunoFunction function when function.Scope == scope:
                    break;
                case JyunoVariable variable:
                    switch (variable.Get())
                    {
                        case StructInstance inner:
                            if (inner.OwnerScope != scope || inner.OwnerName != member || !copy_equivalent(inner))
                                return false;
                            //다른 구조체 값의 영역 안에서 만들어진 구조체 값은 보수적으로 제외
                            for (Scope? outer = inner.Scope.Parent ; outer is not null ; outer = outer.Parent)
                            {
                                if (outer.Instance is not null)
                                    return false;
                            }
                            //구조체 필드의 모든 이름이 멤버 이름으로 선언되어 있어야 함
                            foreach (var (name , inner_holder) in inner.Scope.Variables)
                            {
                                string alias = member + "." + name;
                                if (scope.Aliases is null || !scope.Aliases.TryGetValue(alias , out var target) || target.Instance != inner || target.Name != name
                                    || !scope.Variables.TryGetValue(alias , out object? declared) || !ReferenceEquals(declared , inner_holder))
                                    return false;
                                aliases++;
                            }
                            break;
                        case IList list when is_cloned(list):
                            return false;
                    }
                    break;
                default:
                    return false;
            }
        }
        //그 외의 멤버 이름이 남아있다면 복사본과 다름
        return (scope.Aliases?.Count ?? 0) == aliases;
    }

    //변수에서 값을 읽음: 구조체 값이라면 복사본 (읽은 값을 바꿔도 변수의 구조체 값은 바뀌지 않음)
    object? read(object? value)
    {
        if (value is not StructInstance instance)
            return value;
        StructInstance copy = copy_struct(instance , null);
        copy.Fresh = EscapeAnalysis;
        return copy;
    }

    //변수에 넣을 구조체 값: 방금 만들어진 값이라면 그대로(소유권 이전), 아니라면 복사본
    StructInstance take(StructInstance instance)
    {
        if (instance.Fresh)
        {
            instance.Fresh = false;
            return instance;
        }
        return copy_struct(instance , null);
    }

    //[실험] 탈출 분석: 값이 인터프리터 밖(C# 함수, C# 프로그램)이나 배열/리스트로 빠져나감 → 더 이상 방금 만들어진 값이 아님
    static void escape(object? value)
    {
        if (value is StructInstance instance)
            instance.Fresh = false;
    }
    static void escape(object?[] values)
    {
        foreach (object? value in values)
            escape(value);
    }

    //지금 영역에 변수를 새로 만듦 (구조체 값이라면 복사해서 넣고 멤버 이름을 선언)
    void declare_variable(string name , object? value)
    {
        if (value is not StructInstance instance)
        {
            substitute_variable(name , new JyunoVariable(value));
            return;
        }
        StructInstance copy = take(instance);
        substitute_variable(name , new JyunoVariable(copy));
        bind(current , name , copy);
        refresh_owner(current);
    }

    //이미 있는 변수(holder)에 값을 넣음
    void assign(VariableInterface holder , string name , object? value)
    {
        //구조체 값과 관계없는 대입 (대부분)
        if (value is not StructInstance && !(holder is JyunoVariable && holder.Get() is StructInstance))
        {
            holder.Set(value);
            return;
        }
        object? old = holder is JyunoVariable ? holder.Get() : null;
        StructInstance? copy = value is StructInstance instance ? take(instance) : null;
        //상수라면 여기서 오류가 나며, 아무것도 바뀌지 않음
        holder.Set(copy ?? value);
        //변수가 있는 영역 (프로그램이 제공하는 변수라면 멤버 이름을 선언하지 않음)
        if (!locate(name , holder , out Scope? scope , out string key))
            return;
        if (old is StructInstance previous)
            unbind(previous);
        if (copy is not null)
            bind(scope , key , copy);
        refresh_owner(scope);
    }

    //이름이 가리키는 변수(holder)가 실제로 있는 영역과 이름을 찾음 (멤버 이름이라면 구조체 값의 영역과 필드 이름)
    bool locate(string name , object holder , [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Scope? scope , out string key)
    {
        key = name;
        for (scope = current ; scope is not null ; scope = scope.Parent)
        {
            if (scope.Variables.TryGetValue(name , out object? found) && ReferenceEquals(found , holder))
            {
                while (scope.Aliases is not null && scope.Aliases.TryGetValue(key , out var alias))
                {
                    scope = alias.Instance.Scope;
                    key = alias.Name;
                }
                return true;
            }
        }
        return false;
    }

    //구조체 값(instance)을 담은 변수(scope 영역의 name)의 멤버 이름을 선언
    void bind(Scope scope , string name , StructInstance instance)
    {
        List<(string, object?)> declared = new();
        scope.Aliases ??= new();
        //구조체 값 안의 구조체 값의 멤버 이름(필드.필드)도 함께 선언됨
        foreach (var (member , holder) in instance.Scope.Variables.ToArray())
        {
            string alias = name + "." + member;
            //이미 같은 이름이 있다면 덮어씀
            if (scope.Variables.TryGetValue(alias , out object? existing))
                forget(scope , alias , existing);
            scope.Variables[alias] = holder;
            scope.Aliases[alias] = (instance , member);
            declared.Add((alias , holder));
        }
        instance.OwnerScope = scope;
        instance.OwnerName = name;
        instance.Declared = declared;
        scope_changed(scope);
    }

    //bind로 선언한 멤버 이름을 지움 (그 뒤에 다른 것으로 덮어쓴 이름은 그대로 둠)
    void unbind(StructInstance instance)
    {
        if (instance.OwnerScope is not Scope scope)
            return;
        foreach (var (alias , holder) in instance.Declared!)
        {
            if (scope.Variables.TryGetValue(alias , out object? now) && ReferenceEquals(now , holder))
                scope.Variables.Remove(alias);
            if (scope.Aliases is not null && scope.Aliases.TryGetValue(alias , out var target) && target.Instance == instance)
                scope.Aliases.Remove(alias);
        }
        instance.OwnerScope = null;
        instance.OwnerName = null;
        instance.Declared = null;
        scope_changed(scope);
    }

    //scope 영역의 이름(name)이 holder 대신 다른 것으로 바뀌거나 지워짐
    void forget(Scope scope , string name , object? holder)
    {
        scope.Aliases?.Remove(name);
        if (holder is JyunoVariable variable && variable.Get() is StructInstance instance && instance.OwnerScope == scope && instance.OwnerName == name)
            unbind(instance);
    }

    //구조체 값의 영역에서 멤버 이름이 바뀌었다면, 그 구조체 값을 담은 변수의 멤버 이름도 다시 선언 (바깥으로 계속)
    void refresh_owner(Scope scope)
    {
        while (scope.Instance is StructInstance instance && instance.OwnerScope is Scope owner)
        {
            string name = instance.OwnerName!;
            unbind(instance);
            bind(owner , name , instance);
            scope = owner;
        }
    }

    //구조체 값의 깊은 복사: 필드의 값(구조체 값, 배열, 리스트 포함)을 모두 복사하고, 구조체 함수는 새 구조체 값의 함수로 만듦
    StructInstance copy_struct(StructInstance source , Dictionary<object , object>? copied)
    {
        Scope scope = new(source.Scope.Parent);
        StructInstance copy = new(source.Type , scope) { Members = source.Members };
        foreach (string member in source.Members)
        {
            if (!source.Scope.Variables.TryGetValue(member , out object? holder))
                continue;
            scope.Variables[member] = holder switch {
                JyunoFunction function when function.Scope == source.Scope => function.Rebind(scope),
                JyunoStructType type when type.Scope == source.Scope => type.Rebind(scope),
                JyunoConstantVariable constant => new JyunoConstantVariable(copy_value(constant.Get() , ref copied)),
                JyunoVariable variable => new JyunoVariable(copy_value(variable.Get() , ref copied)),
                _ => holder
            };
        }
        foreach (string member in source.Members)
        {
            if (scope.Variables.TryGetValue(member , out object? holder) && holder is JyunoVariable variable && variable.Get() is StructInstance inner)
                bind(scope , member , inner);
        }
        return copy;
    }

    //구조체 필드의 값을 깊은 복사 (같은 배열/리스트를 여러번 담고 있다면 복사본도 같은 복사본을 담음)
    object? copy_value(object? value , ref Dictionary<object , object>? copied)
    {
        switch (value)
        {
            case StructInstance instance:
                return copy_struct(instance , copied);
            case Array array when is_cloned(array):
            {
                copied ??= new(ReferenceEqualityComparer.Instance);
                if (copied.TryGetValue(array , out object? done))
                    return done;
                Array clone = (Array)array.Clone();
                copied[array] = clone;
                if (needs_copy(array.GetType().GetElementType()!))
                {
                    for (int i = 0 ; i < clone.Length ; i++)
                        clone.SetValue(copy_value(array.GetValue(i) , ref copied) , i);
                }
                return clone;
            }
            case IList list when is_cloned(list):
            {
                copied ??= new(ReferenceEqualityComparer.Instance);
                if (copied.TryGetValue(list , out object? done))
                    return done;
                IList clone = (IList)Activator.CreateInstance(list.GetType() , list.Count)!;
                copied[list] = clone;
                bool deep = needs_copy(element_type(list));
                foreach (object? item in list)
                    clone.Add(deep ? copy_value(item , ref copied) : item);
                return clone;
            }
            //그 외의 값(숫자, 문자열 등)은 바꿀수 없으므로 그대로 (프로그램이 제공한 다른 컬렉션도 그대로)
            default:
                return value;
        }
    }
    //구조체 값을 복사할 때 따로 복사되는 컬렉션인지 (배열과 List<T>, 프로그램이 제공한 다른 컬렉션은 그대로 공유)
    static bool is_cloned(IList list) => list is Array array ? array.Rank is 1 : list.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(List<>);
    static Type element_type(IList cloned) => cloned is Array ? cloned.GetType().GetElementType()! : cloned.GetType().GetGenericArguments()[0];
    //원소 타입이 구조체 값이나 컬렉션을 담을수 있는지 (long, string 등은 복사할 필요 없음)
    static bool needs_copy(Type element) => !(element.IsValueType || element == typeof(string));
}
