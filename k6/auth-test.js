import http from 'k6/http';
import { check } from 'k6';

export let options = {
    vus: 20,
    duration: '20s',
};

export default function () {

    // LOGIN REQUEST
    let loginPayload = JSON.stringify({
        username: 'admin',
        password: 'Admin@123'
    });

    let loginParams = {
        headers: {
            'Content-Type': 'application/json',
        },
    };

    let loginResponse = http.post(
        'http://localhost:65084/api/auth/login',
        loginPayload,
        loginParams
    );

    check(loginResponse, {
        'login successful': (r) => r.status === 200,
    });

    // EXTRACT TOKEN
    let token =
        JSON.parse(loginResponse.body).token;

    // USE TOKEN
    let authParams = {
        headers: {
            'Authorization': `Bearer ${token}`
        }
    };

    let productResponse = http.get(
        'http://localhost:65084/api/products',
        authParams
    );

    check(productResponse, {
        'products fetched': (r) => r.status === 200,
    });
}