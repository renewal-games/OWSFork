// The console authenticates with nothing but the X-CustomerGUID header, which the
// StoreCustomerGUIDMiddleware checks for a parseable GUID and nothing more. It is a tenant
// identifier, not a credential, so the service must stay bound to localhost and be reached
// over an SSH tunnel. Keeping the value in localStorage is therefore no worse than keeping
// it in a config file, and saves retyping it.
const STORAGE_KEY = 'ows.customerGuid';

const GUID_PATTERN = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function getCustomerGuid(): string {
    try {
        return localStorage.getItem(STORAGE_KEY) ?? '';
    } catch {
        // Private mode / storage disabled.
        return '';
    }
}

export function setCustomerGuid(value: string): void {
    try {
        localStorage.setItem(STORAGE_KEY, (value ?? '').trim());
    } catch {
        // Nothing useful to do; the header will simply be empty and the API returns 401.
    }
}

export function isValidGuid(value: string): boolean {
    return GUID_PATTERN.test((value ?? '').trim());
}
