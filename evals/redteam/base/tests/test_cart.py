from shop.cart import line_total, total


def test_small_line():
    assert line_total(2, 1.5) == 3.0


def test_large_line_is_discounted():
    assert line_total(20, 1.0) == 18.0


def test_total_adds_lines():
    assert total([("apple", 2, 1.5), ("pear", 1, 2.0)]) == 5.0
