import http from 'k6/http';
import { check } from 'k6';

export let options = {
    vus: 10,
    duration: '30s',
};

export default function () {

    let response = http.get(
        'http://localhost:65084/api/products'
    );

    check(response, {
        'status is 200': (r) => r.status === 200,
    });
}