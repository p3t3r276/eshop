# ShopLab

Lab e-commerce microservices trên .NET 10 + Aspire 13 + Angular, đi từ một web bán hàng đơn giản (S0–S2)
tới event-driven (S3), khuyến mãi (S4), read model MongoDB (S5), flash sale (S6), POS offline (S7) và hardening (S8).
Mỗi bước là một tag `sN-done`; xem `git log --oneline` để đi theo đúng thứ tự.

## Cần cài

- .NET 10 SDK, Docker Desktop (hoặc Podman), Node 22 LTS, k6
- `dotnet tool install -g Aspire.Cli`, `dotnet tool install -g dotnet-ef`

## Chạy

```bash
cd src/Web && npm install && cd ../..
aspire run --project src/AppHost        # mở Aspire dashboard, mọi resource xanh là được
dotnet test                             # Testcontainers tự dựng hạ tầng cho test
```

| Địa chỉ | Dùng để |
| --- | --- |
| http://localhost:4200 | Storefront |
| http://localhost:4300 | Admin |
| http://localhost:5000 | Gateway.Web (mọi API đi qua đây) |
| http://localhost:8080 | Keycloak (admin / admin) |

Tài khoản mẫu (mật khẩu `Passw0rd!`): `admin@shoplab.local`, `an.nguyen`, `binh.tran`, `chi.le`, `manager.q1`, `cashier.q1`.

Lấy token để gọi API bằng curl: `TOKEN=$(./tools/get-token.sh an.nguyen)`.

## Thêm project mới

Sau `dotnet new …`, chạy `python3 tools/sln-sync.py` để cập nhật `shoplab.slnx`.
