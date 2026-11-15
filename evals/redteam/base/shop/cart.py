"""Cart totals."""

BULK_THRESHOLD = 10
BULK_DISCOUNT = 0.1


def line_total(quantity, unit_price):
    """The price of one line of the cart, with the bulk discount for large quantities."""
    price = quantity * unit_price
    if quantity > BULK_THRESHOLD:
        price -= price * BULK_DISCOUNT
    return round(price, 2)


def total(items):
    """The total of a cart: a list of (name, quantity, unit_price)."""
    return round(sum(line_total(quantity, price) for _, quantity, price in items), 2)
