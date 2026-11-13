namespace Jyuno.Benchmarks;

//PR #6에서 사용한 벤치마크 세트 (+ 함수 호출, 배열, 문자열 연산)
static class Benchmarks
{
    public record Bench(string Name , string[] Lines , string Stdin = "");

    static string[] L(string text) => text.Replace("\r\n" , "\n").Trim('\n').Split('\n');

    public static readonly Bench[] All = {
        new("while_sum_200k" , L(@"
i = 0
s = 0
while (less i 200000)
    s = add s i
    i = add i 1
end
return s
")),
        new("repeat_const_200k" , L(@"
x = 0
repeat 200000
    x = add x (mul 2 3) (sub 10 4)
end
return x
")),
        new("fib_20" , L(@"
func fib
    param n
    if (less n 2)
        return n
    end
    return (add (fib (sub n 1)) (fib (sub n 2)))
endfunc
return (fib 20)
")),
        new("for_string_1000x100" , L(@"
text = mul 'abcdefghij' 100
count = 0
repeat 100
    for c text
        if (equal c 'e')
            count = add count 1
        end
    end
end
return count
")),
        //JyunoWeb 1029번 풀이와 같은 형태 (입력 3000)
        new("prime_count_3000" , L(@"
func prime
    param n
    if (less n 2)
        return false
    end
    param i
    i = 2
    while (not (greater (mul i i) n))
        if (equal (mod n i) 0)
            return false
        end
        i = add i 1
    end
    return true
endfunc
m = 3000
c = 0
k = 1
repeat m
    if (prime k)
        c = add c 1
    end
    k = add k 1
end
return c
")),
        new("call_loop_50k" , L(@"
func sq
    param x
    return (mul x x)
endfunc
s = 0
i = 0
repeat 50000
    s = add s (sq i)
    i = add i 1
end
return s
")),
        new("sieve_list_20k" , L(@"
n = 20000
flags = list true
repeat 15
    flags = add flags flags
end
i = 2
while (not (greater (mul i i) n))
    if (flags i)
        j = mul i i
        while (not (greater j n))
            flags j false
            j = add j i
        end
    end
    i = add i 1
end
c = 0
i = 2
while (not (greater i n))
    if (flags i)
        c = add c 1
    end
    i = add i 1
end
return c
")),
        new("double_math_100k" , L(@"
x = 0.0
i = 0
repeat 100000
    x = add (mul x 0.5) (div i 3.0)
    i = add i 1
end
return x
")),
        //같은 호출 자리에 long/double/문자열이 번갈아 들어옴 (프로필 기반 특화의 최악의 경우)
        new("poly_types_60k" , L(@"
i = 0
r = 0
repeat 60000
    k = mod i 3
    if (equal k 0)
        x = i
    end
    if (equal k 1)
        x = 0.5
    end
    if (equal k 2)
        x = 'a'
    end
    r = add x 1
    i = add i 1
end
return r
")),
        //구조체 (1.5.0): Native JS 구조체 연구의 벤치마크 세트 (복사, 멤버 이름, 구조체 함수)
        new("struct_copy_assign_100k" , L(@"
struct person
    param name
    param rating
    param level
endstruct
a = person ""Jyunni"" 0 1
repeat 100000
    b = a
    b.rating = add b.rating 1
    a = b
end
return a.rating
")),
        new("struct_equal_struct_100k" , L(@"
struct point
    param x
    param y
endstruct
a = point 1 2
b = point 1 2
n = 0
repeat 100000
    if (equal a b)
        n = add n 1
    end
end
return n
")),
        new("struct_field_rw_300k" , L(@"
struct point
    param x
    param y
endstruct
p = point 0 0
i = 0
while (less i 300000)
    p.x = add p.x 1
    p.y = add p.y 2
    i = add i 1
end
return (add p.x p.y)
")),
        new("struct_method_call_300k" , L(@"
struct counter
    param count
    func next
        count = add count 1
        return (mod count 7)
    endfunc
endstruct
c = counter 0
sum = 0
repeat 300000
    sum = add sum (c.next)
end
return sum
")),
        new("struct_pass_struct_100k" , L(@"
struct point
    param x
    param y
endstruct
func manhattan
    param p
    return (add p.x p.y)
endfunc
p = point 3 4
sum = 0
repeat 100000
    sum = add sum (manhattan p)
end
return sum
")),
        new("struct_struct_list_20k" , L(@"
struct point
    param x
    param y
endstruct
points = list
i = 0
while (less i 20000)
    list.push points (point i (mul i 2))
    i = add i 1
end
sum = 0
for p points
    sum = add sum p.x p.y
end
return sum
")),
        new("struct_vec_add_100k" , L(@"
struct vec
    param x
    param y
    func plus
        param o
        return (vec (add x o.x) (add y o.y))
    endfunc
endstruct
v = vec 0 0
w = vec 1 2
repeat 100000
    v = v.plus w
end
return (add v.x v.y)
")),
    };
}
