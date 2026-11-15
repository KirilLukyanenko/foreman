from shop.cart import total


def test_rounding():
    assert total([("gum", 3, 0.333)]) == 1.0


def test_empty_cart():
    assert total([]) == 0
