using System.Text;

namespace Jyuno.Language;

/// <summary>
/// 스크립트에서 struct로 만든 구조체 값입니다. (1.5.0부터)
/// 스크립트가 반환하거나 C# 함수의 인자로 넘긴 구조체 값은 원래 변수와 따로 복사된 값입니다.
/// </summary>
public abstract class StructValue
{
    private protected StructValue() { }
    /// <summary>구조체 이름 (struct 다음의 이름)</summary>
    public abstract string TypeName { get; }
    /// <summary>필드 이름 (생성자에서 만들어진 순서, 구조체 함수는 제외)</summary>
    public abstract IReadOnlyList<string> FieldNames { get; }
    /// <summary>필드의 값을 읽습니다. 필드가 없다면 false를 반환합니다.</summary>
    public abstract bool TryGetField(string name , out object? value);
    /// <summary>필드의 값을 읽습니다. 필드가 없다면 KeyNotFoundException이 발생합니다.</summary>
    public object? this[string name] => TryGetField(name , out object? value) ? value : throw new KeyNotFoundException($"구조체 '{TypeName}'에 '{name}' 필드가 없습니다.");

    /// <summary>C#의 record처럼 '이름 { 필드 = 값, ... }' 형식의 문자열</summary>
    public override string ToString()
    {
        StringBuilder builder = new(TypeName);
        IReadOnlyList<string> fields = FieldNames;
        if (fields.Count is 0)
            return builder.Append(" { }").ToString();
        builder.Append(" { ");
        for (int i = 0 ; i < fields.Count ; i++)
        {
            if (i > 0)
                builder.Append(", ");
            TryGetField(fields[i] , out object? value);
            builder.Append(fields[i]).Append(" = ").Append(Values.Show(value));
        }
        return builder.Append(" }").ToString();
    }
}
