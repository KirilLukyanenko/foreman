ASP.NET Core API returns 500 for orders dated in the future

We run a small order-management API (ASP.NET Core 8, EF Core, PostgreSQL). Since last week, POST /api/orders
returns a 500 when the customer picks a delivery date more than 30 days ahead. It should return a 400 with a clear
validation message instead, like the other validation errors do.

What we need:
- fix the bug so the endpoint returns 400 with the message "Delivery date must be within 30 days"
- add a unit test for it
- don't change the public API contract, our mobile app depends on it

The repo is on GitHub, I will give you access after we agree. Should take a couple of hours for someone who knows
.NET. Fixed price $80, I need it by Wednesday.

To show you actually read this, start your proposal with the words "blue heron".
