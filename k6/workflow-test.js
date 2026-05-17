import http from 'k6/http';
import { check, sleep } from 'k6';

export let options = {

    vus: 100,
    duration: '1m',

    thresholds: {

        http_req_duration: ['p(95)<1000'],

        http_req_failed: ['rate<0.05']
    }
};

export default function () {

    // =========================
    // LOGIN
    // =========================

    let loginPayload = JSON.stringify({
        username: 'admin',
        password: 'Admin@123'
    });

    let loginResponse = http.post(
        'http://localhost:65084/api/auth/login',
        loginPayload,
        {
            headers: {
                'Content-Type': 'application/json'
            }
        }
    );

    check(loginResponse, {
        'login successful': (r) => r.status === 200,
    });

    let token =
        JSON.parse(loginResponse.body).token;

    let authHeaders = {
        headers: {
            'Authorization': `Bearer ${token}`,
            'Content-Type': 'application/json'
        }
    };

    sleep(1);

    // =========================
    // GET PRODUCTS
    // =========================
// =========================
// GET PRODUCTS
// =========================

let productsResponse = http.get(
    'http://localhost:65084/api/products?categoryId=18&page=1&pageSize=50',
    authHeaders
);

check(productsResponse, {
    'products fetched': (r) => r.status === 200,
});

let products =
    JSON.parse(productsResponse.body);

// GLOBAL VARIABLE
let productId = null;

// VALIDATE RESPONSE
if (
    products.items &&
    products.items.length > 0
) {

    productId =
        products.items[0].productId;

    console.log(
        `Using Product ID: ${productId}`
    );
}
else {

    console.log(
        'No products returned from API'
    );

    return;
}

sleep(1);

// =========================
// PRODUCT DETAILS
// =========================

let productDetailResponse = http.get(
    `http://localhost:65084/api/products/${productId}`,
    authHeaders
);

check(productDetailResponse, {
    'product details fetched': (r) =>
        r.status === 200,
});

sleep(1);
    // =========================
    // ADD TO CART
    // =========================

    let cartPayload = JSON.stringify({
        productId: productId,
        quantity: 1
    });

    let cartResponse = http.post(
        'http://localhost:65084/api/cart',
        cartPayload,
        authHeaders
    );

    check(cartResponse, {
        'cart updated': (r) =>
            r.status === 200 || r.status === 201,
    });

    sleep(1);

    // =========================
    // CREATE ORDER
    // =========================

let orderPayload = JSON.stringify({
    addressId: 1,
    paymentMethodId: 1,
    notes: 'k6 performance order'
});

    let orderResponse = http.post(

        'http://localhost:65084/api/orders',
        orderPayload,
        authHeaders
    );
		console.log(orderResponse.status);
console.log(orderResponse.body);

    check(orderResponse, {
        'order created': (r) =>
            r.status === 200 || r.status === 201,
    });
console.log(productsResponse.body);
    sleep(1);
}