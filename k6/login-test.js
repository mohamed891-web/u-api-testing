import http from 'k6/http';
import { check } from 'k6';

export let options = {
    vus: 20,
    duration: '20s',
};

export default function () {

    let payload = JSON.stringify({
        username: 'admin',
        password: 'Admin@123'
    });

    let params = {
        headers: {
            'Content-Type': 'application/json',
        },
    };

    let response = http.post(
        'http://localhost:65084/api/auth/login',
        payload,
        params
    );

    check(response, {
        'login successful': (r) => r.status === 200,
    });
}