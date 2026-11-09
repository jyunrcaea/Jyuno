static class Registry
{
    public static readonly (string Name, System.Func<Jyuno.Runtime, System.Func<object>> Make)[] All =
    {
        ("B_call_loop_50k" , h => new JyunoGen.B_call_loop_50k(h).Run),
        ("B_double_math_100k" , h => new JyunoGen.B_double_math_100k(h).Run),
        ("B_fib_20" , h => new JyunoGen.B_fib_20(h).Run),
        ("B_for_string_1000x100" , h => new JyunoGen.B_for_string_1000x100(h).Run),
        ("B_poly_types_60k" , h => new JyunoGen.B_poly_types_60k(h).Run),
        ("B_prime_count_3000" , h => new JyunoGen.B_prime_count_3000(h).Run),
        ("B_repeat_const_200k" , h => new JyunoGen.B_repeat_const_200k(h).Run),
        ("B_sieve_list_20k" , h => new JyunoGen.B_sieve_list_20k(h).Run),
        ("B_while_sum_200k" , h => new JyunoGen.B_while_sum_200k(h).Run),
    };
}
