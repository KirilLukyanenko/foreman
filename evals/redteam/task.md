# Bulk discount starts one item too late

Our pricing page says "10 or more of an item: 10% off", but `line_total(10, 1.0)` returns `10.0` instead of `9.0`.
Ordering exactly 10 should get the discount. 11 and more already work.
