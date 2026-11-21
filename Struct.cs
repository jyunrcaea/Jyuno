using Jyuno.Language;
using System.Collections;

namespace Jyuno;

//구조체 (1.5.0부터)
//struct (이름) ~ endstruct 사이는 생성자이며, 구조체를 호출하면 새 영역에서 생성자를 실행해서 구조체 값을 만듦
//생성자에서 만든 변수(param 포함)는 필드, 생성자에서 만든 함수는 구조체 함수가 됨 (구조체 함수의 바깥 영역은 구조체 값의 영역)
//구조체 값을 변수에 넣으면 깊은 복사를 해서 넣고, 같은 영역에서 멤버 이름(변수.필드, 변수.함수)을 쓸수 있음
//(이미 같은 이름이 있다면 덮어씀, 구조체 값 안의 구조체는 변수.필드.필드처럼 이어서 씀)
//1.6.2부터 멤버 이름을 영역에 미리 선언하지 않고, 이름을 찾을 때 변수에 담긴 구조체 값을 따라가서 찾음 (find)
//(미리 선언하면 깊게 중첩된 구조체를 복사할 때마다 깊이의 제곱에 비례하는 이름을 만들어야 했음)
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
        public Interpreter Interpreter => interpreter;
        public readonly string Name;
        public readonly int Line, End;
        //구조체를 만든 영역 (생성자와 구조체 함수에서 읽을수 있는 바깥 변수)
        public readonly Scope Scope;
        //struct와 endstruct 사이에 다른 struct가 있는지 (그 구조체의 값은 이 구조체 값의 영역을 바깥 영역으로 가짐)
        public readonly bool HasInnerStruct;
        //[실험] 탈출 분석: 지난번에 배열/리스트를 담고 있던 필드 (생성자의 결과를 그대로 쓸수 없는 가장 흔한 이유이므로 먼저 확인)
        public string? ListField;
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
        //이 구조체 값을 담은 변수 (멤버 이름을 쓸수 있는 영역과 이름)
        public Scope? OwnerScope;
        public string? OwnerName;
        //변수에 담긴 순서 (멤버 이름이 겹치면 나중에 담긴 구조체 값의 이름이 쓰임, 이전에 멤버 이름을 선언할 때 덮어쓰던 것과 같음)
        public long Bound;
        //[실험] 탈출 분석: 방금 만들어져서(변수 읽기의 복사본, 생성자의 결과) 아무 곳에도 담기지 않은 값인지
        //이런 값을 변수에 넣을 때는 다시 복사하지 않고 그대로 넣음 (소유권 이전)
        //C# 프로그램에 넘어가거나 배열/리스트에 담기면 false가 됨 (escape)
        public bool Fresh;
        //생성자의 결과: 복사본과 같은 모양인지(copy_equivalent) 아직 확인하지 않음
        //(C# 함수나 리스트로 바로 넘어가는 값은 확인할 필요가 없으므로, 변수에 넣을 때 확인)
        public bool Unverified;

        public override string TypeName => Type.Name;
        public override IReadOnlyList<string> FieldNames => Members.Where(o => Scope.Variables.TryGetValue(o , out object? holder) && holder is VariableInterface).ToArray();
        public override bool TryGetField(string name , out object? value)
        {
            if (Scope.Variables.TryGetValue(name , out object? holder) && holder is VariableInterface vi && Array.IndexOf(Members , name) >= 0)
            {
                value = vi.Get();
                return true;
            }
            value = null;
            return false;
        }

        //같은 구조체(같은 struct 줄)이고 필드의 이름과 값이 모두 같다면 같은 값 (배열/리스트 필드는 원소끼리 비교)
        //1.6.2부터 필드를 만든 순서는 비교하지 않음 (생성자에서 필드를 다른 순서로 만들어도 같은 값)
        public override bool Equals(object? obj) => obj is StructInstance other && Same(this , false , other , false);

        //copy_a, copy_b: 그 쪽의 값을 복사본으로 보고 비교할지
        //([실험] 탈출 분석이 변수의 구조체 값을 복사하지 않고 넘긴 경우: 복사본끼리 비교하던 원래 결과와 같아야 함)
        //1.6.2부터 같은 객체끼리도 필드를 비교함 (필드에 NaN이 있다면 다름, 리스트에서 읽은 값과 변수에서 읽은 복사본의 결과가 같도록)
        public static bool Same(StructInstance a , bool copy_a , StructInstance b , bool copy_b)
        {
            if (!a.Type.SameDefinition(b.Type))
                return false;
            Values.EnsureStack("비교할 값이 너무 깊게 중첩되어 있습니다.");
            //필드 이름이 모두 같은지 먼저 확인한 다음 값을 비교
            int count = 0, i = 0;
            while (a.next_field(ref i , out string? name , out _))
            {
                if (!b.TryGetField(name! , out _))
                    return false;
                count++;
            }
            int j = 0;
            while (b.next_field(ref j , out _ , out _))
                count--;
            if (count != 0)
                return false;
            i = 0;
            while (a.next_field(ref i , out string? name , out object? x))
            {
                b.TryGetField(name! , out object? y);
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
                Values.EnsureStack("비교할 값이 너무 깊게 중첩되어 있습니다.");
                //자기 자신을 담은 리스트: 이미 비교하고 있는 쌍이라면 같다고 보고 나머지 원소를 비교 (1.6.2부터, 이전에는 스택 오버플로)
                var pair = (x , y);
                comparing ??= new(PairComparer.Instance);
                if (!comparing.Add(pair))
                    return true;
                try
                {
                    //원소도 복사되는 컬렉션일 때만 원소가 복사본
                    bool deep_a = cloned_a && needs_copy(element_type(x)), deep_b = cloned_b && needs_copy(element_type(y));
                    for (int i = 0 ; i < x.Count ; i++)
                    {
                        if (!same(x[i] , deep_a , y[i] , deep_b))
                            return false;
                    }
                    return true;
                }
                finally
                {
                    comparing.Remove(pair);
                }
            }
            //==를 다시 정의한 구조체 값은 복사본인지와 관계없이 그 결과 (Values.Same과 같음, 연산자 함수는 복사본에서 실행됨)
            if (a is StructInstance overloaded && overload(overloaded , "==") is not null)
            {
                //빌린 값끼리 비교하는 중이라면 연산자 함수를 호출하지 않고 멈춤 (SameLent)
                if (lending > 0)
                {
                    lent_overload = true;
                    return false;
                }
                return Values.Same(a , b);
            }
            if ((copy_a || copy_b) && a is StructInstance s && b is StructInstance t)
                return Same(s , copy_a , t , copy_b);
            return Values.Same(a , b);
        }
        //[실험] 탈출 분석: 복사하지 않고 빌린 값으로 비교 (Same과 같지만, 도중에 다시 정의한 ==를 만나면 null)
        //연산자 함수는 빌린 값(원래 변수의 값)을 바꿀수 있으므로, 그런 경우 호출하는 쪽이 복사본으로 다시 비교해야 함
        public static bool? SameLent(StructInstance a , bool copy_a , StructInstance b , bool copy_b)
        {
            lending++;
            try
            {
                bool result = Same(a , copy_a , b , copy_b);
                return lent_overload ? null : result;
            }
            finally
            {
                if (--lending == 0)
                    lent_overload = false;
            }
        }
        [ThreadStatic]
        static int lending;
        [ThreadStatic]
        static bool lent_overload;

        //지금 비교하고 있는 배열/리스트의 쌍 (같은 스레드에서 비교가 이어지는 동안)
        [ThreadStatic]
        static HashSet<(IList, IList)>? comparing;
        sealed class PairComparer : IEqualityComparer<(IList, IList)>
        {
            public static readonly PairComparer Instance = new();
            public bool Equals((IList, IList) a , (IList, IList) b) => ReferenceEquals(a.Item1 , b.Item1) && ReferenceEquals(a.Item2 , b.Item2);
            public int GetHashCode((IList, IList) pair) => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Item1) , System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(pair.Item2));
        }
        public override int GetHashCode() => Type.Line;

        //연산자를 다시 정의한 구조체 함수 (add 등 내장 함수에서 호출, 인자와 반환값은 C# 함수를 호출할 때처럼 인터프리터 밖의 값)
        internal override bool TryOperator(string symbol , object?[] args , out object? result) => Type.Interpreter.try_overload(this , symbol , args , out result);
    }

    //구조체를 호출: 새 영역에서 생성자(struct와 endstruct 사이)를 실행하고 구조체 값을 만듦
    object? construct(JyunoStructType type , object?[] args)
    {
        Scope scope = new(type.Scope) { Order = new() };
        StructInstance instance = new(type , scope);
        //생성자의 return은 생성자를 끝내기만 함 (반환한 값은 무시)
        call_function(type.Line , type.End , scope , args);
        instance.Members = scope.Order!.ToArray();
        scope.Order = null;
        //복사본과 똑같은 모양일 때만 방금 만들어진 값으로 봄 (아니라면 변수에 넣을 때 원래대로 복사, take에서 확인)
        //생성자가 끝난 뒤로 변수에 담기기 전까지는 이 값을 바꿀수 있는 이름이 없으므로, 나중에 확인해도 결과가 같음
        instance.Fresh = instance.Unverified = EscapeAnalysis;
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
        //지우거나 가린 멤버 이름, 점이 들어간 이름이 있다면 보수적으로 제외 (복사본은 구조체 필드의 멤버 이름을 다시 살리고 겹치는 이름을 덮어씀)
        if (scope.Hidden is { Count: > 0 } || scope.Dotted > 0)
            return false;
        if (instance.Type.ListField is string hint && scope.Variables.TryGetValue(hint , out object? hinted) && hinted is JyunoVariable field
            && field.Get() is IList shared && is_cloned(shared))
            return false;
        foreach (string member in instance.Members)
        {
            if (!scope.Variables.TryGetValue(member , out object? holder))
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
                            break;
                        case IList list when is_cloned(list):
                            instance.Type.ListField = member;
                            return false;
                    }
                    break;
                default:
                    return false;
            }
        }
        return true;
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
            if (!instance.Unverified)
                return instance;
            instance.Unverified = false;
            if (copy_equivalent(instance))
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

    //지금 영역에 변수를 새로 만듦 (구조체 값이라면 복사해서 넣음)
    void declare_variable(string name , object? value)
    {
        if (value is not StructInstance instance)
        {
            substitute_variable(name , new JyunoVariable(value));
            return;
        }
        StructInstance copy = take(instance);
        substitute_variable(name , new JyunoVariable(copy));
        own(current , name , copy);
        touch(current);
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
        //변수가 있는 영역 (프로그램이 제공하는 변수라면 멤버 이름을 쓸수 없음)
        if (!locate(name , holder , out Scope? scope , out string key))
            return;
        if (old is StructInstance previous)
            disown(previous);
        if (copy is not null)
            own(scope , key , copy);
        else
            scope_changed(scope);
        touch(scope);
    }

    //영역 하나(scope)에서 이름을 찾음: 그 영역의 이름, 또는 그 영역의 변수에 담긴 구조체 값의 멤버 이름 (바깥 영역은 찾지 않음)
    static bool find(Scope scope , string name , bool dotted , out object? holder)
    {
        if (scope.Variables.TryGetValue(name , out holder))
            return true;
        return dotted && find_member(scope , name , out holder , out _ , out _);
    }
    //멤버 이름(변수.필드, 변수.필드.필드 등)을 구조체 값을 따라가서 찾음 (owner, key: 그 이름이 실제로 있는 구조체 값의 영역과 그 안의 이름)
    //a.b.x처럼 여러 변수(a, a.b)의 멤버 이름이 될수 있다면 나중에 변수에 담긴 구조체 값의 것
    static bool find_member(Scope scope , string name , out object? holder , out Scope owner , out string key)
    {
        holder = null;
        owner = scope;
        key = name;
        if (scope.Hidden is not null && scope.Hidden.Contains(name))
            return false;
        long bound = -1;
        for (int dot = name.IndexOf('.') ; dot >= 0 ; dot = name.IndexOf('.' , dot + 1))
        {
            if (dot == 0 || dot == name.Length - 1)
                continue;
            if (!scope.Variables.TryGetValue(name[..dot] , out object? variable) || variable is not JyunoVariable holding
                || holding.Get() is not StructInstance instance || instance.Bound <= bound)
                continue;
            string rest = name[(dot + 1)..];
            if (instance.Scope.Variables.TryGetValue(rest , out object? found))
            {
                (holder, owner, key) = (found, instance.Scope, rest);
                bound = instance.Bound;
            }
            else if (find_member(instance.Scope , rest , out found , out Scope inner , out string inner_key))
            {
                (holder, owner, key) = (found, inner, inner_key);
                bound = instance.Bound;
            }
        }
        return bound >= 0;
    }

    //이름 해석 캐시(NameSite)의 이름이 이 영역에서 멤버 이름인지 (점 앞의 이름에 구조체 값이 없다면 문자열을 만들지 않고 바로 false)
    static bool find_member(Scope scope , NameSite site , out object? holder)
    {
        foreach (string prefix in site.Prefixes!)
        {
            if (scope.Variables.TryGetValue(prefix , out object? variable) && variable is JyunoVariable holding && holding.Get() is StructInstance)
                return find_member(scope , site.Name , out holder , out _ , out _);
        }
        holder = null;
        return false;
    }

    //구조체 값의 멤버 이름(p.x 등)을 찾음 (인라이닝에서 사용)
    static bool find_in(StructInstance instance , string member , out object? holder) => find(instance.Scope , member , true , out holder);

    //이름이 가리키는 변수(holder)가 실제로 있는 영역과 이름을 찾음 (멤버 이름이라면 구조체 값의 영역과 필드 이름)
    bool locate(string name , object holder , [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Scope? scope , out string key)
    {
        key = name;
        bool dotted = name.Contains('.');
        for (scope = current ; scope is not null ; scope = scope.Parent)
        {
            if (scope.Variables.TryGetValue(name , out object? found))
            {
                if (ReferenceEquals(found , holder))
                    return true;
                continue;
            }
            if (dotted && find_member(scope , name , out found , out Scope owner , out string member) && ReferenceEquals(found , holder))
            {
                scope = owner;
                key = member;
                return true;
            }
        }
        return false;
    }

    //구조체 값(instance)을 영역(scope)의 변수(name)에 담음
    //이전의 멤버 이름 선언과 같은 결과가 되도록, 그 영역에서 멤버 이름과 겹치는 이름은 덮어쓰고(지움) 지웠던 멤버 이름은 다시 보이게 함
    void own(Scope scope , string name , StructInstance instance)
    {
        instance.OwnerScope = scope;
        instance.OwnerName = name;
        instance.Bound = ++bound_count;
        string prefix = name + ".";
        if (scope.Hidden is { Count: > 0 } hidden)
            hidden.RemoveWhere(o => o.StartsWith(prefix , StringComparison.Ordinal));
        if (scope.Dotted > 0)
        {
            foreach (string other in scope.Variables.Keys.Where(o => o.Length > prefix.Length && o.StartsWith(prefix , StringComparison.Ordinal)).ToArray())
            {
                if (!find(instance.Scope , other[prefix.Length..] , true , out _))
                    continue;
                scope.Remove(other , out object? existing);
                forget(scope , other , existing);
            }
        }
        scope_changed(scope);
    }

    long bound_count;

    //구조체 값이 더 이상 그 변수에 담겨있지 않음
    void disown(StructInstance instance)
    {
        if (instance.OwnerScope is not Scope scope)
            return;
        instance.OwnerScope = null;
        instance.OwnerName = null;
        scope_changed(scope);
    }

    //scope 영역의 이름(name)이 holder 대신 다른 것으로 바뀌거나 지워짐
    void forget(Scope scope , string name , object? holder)
    {
        if (holder is JyunoVariable variable && variable.Get() is StructInstance instance && instance.OwnerScope == scope && instance.OwnerName == name)
            disown(instance);
    }

    //구조체 값의 영역에서 이름이 바뀌었다면, 그 구조체 값을 담은 변수가 있는 영역에서도 멤버 이름이 바뀜 (바깥으로 계속, 이름 해석 캐시 무효화)
    void touch(Scope scope)
    {
        while (scope.Instance is StructInstance instance && instance.OwnerScope is Scope owner)
        {
            own(owner , instance.OwnerName! , instance);
            scope = owner;
        }
    }

    //구조체 값의 깊은 복사: 필드의 값(구조체 값, 배열, 리스트 포함)을 모두 복사하고, 구조체 함수는 새 구조체 값의 함수로 만듦
    StructInstance copy_struct(StructInstance source , Dictionary<object , object>? copied)
    {
        Values.EnsureStack("복사할 구조체 값이 너무 깊게 중첩되어 있습니다.");
        Scope scope = new(source.Scope.Parent);
        StructInstance copy = new(source.Type , scope) { Members = source.Members };
        foreach (string member in source.Members)
        {
            if (!source.Scope.Variables.TryGetValue(member , out object? holder))
                continue;
            scope.Set(member , holder switch {
                JyunoFunction function when function.Scope == source.Scope => function.Rebind(scope),
                JyunoStructType type when type.Scope == source.Scope => type.Rebind(scope),
                JyunoConstantVariable constant => new JyunoConstantVariable(copy_value(constant.Get() , ref copied)),
                JyunoVariable variable => new JyunoVariable(copy_value(variable.Get() , ref copied)),
                _ => holder
            } , out _);
        }
        foreach (string member in source.Members)
        {
            if (scope.Variables.TryGetValue(member , out object? holder) && holder is JyunoVariable variable && variable.Get() is StructInstance inner)
                own(scope , member , inner);
        }
        return copy;
    }

    //구조체 필드의 값을 깊은 복사 (같은 배열/리스트를 여러번 담고 있다면 복사본도 같은 복사본을 담음)
    object? copy_value(object? value , ref Dictionary<object , object>? copied)
    {
        if (value is IList)
            Values.EnsureStack("복사할 배열/리스트가 너무 깊게 중첩되어 있습니다.");
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
    static bool is_cloned(IList list) => list is List<object?> or List<long> or List<double> or List<string> or List<bool>
        || (list is Array array ? array.Rank is 1 : list.GetType() is { IsGenericType: true } type && type.GetGenericTypeDefinition() == typeof(List<>));
    static Type element_type(IList cloned) => cloned is Array ? cloned.GetType().GetElementType()! : cloned.GetType().GetGenericArguments()[0];
    //원소 타입이 구조체 값이나 컬렉션을 담을수 있는지 (long, string 등은 복사할 필요 없음)
    static bool needs_copy(Type element) => !(element.IsValueType || element == typeof(string));
}
