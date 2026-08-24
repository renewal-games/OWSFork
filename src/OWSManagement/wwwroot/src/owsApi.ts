import { owsApiClient } from "../src/owsApiClient";

const client: owsApiClient = new owsApiClient("/api");

export default {

    getUsers() {
        return client.get('/Users');
    },
    getRoles() {
        return client.get('/Users/Roles');
    },
    addUser(data: Record<string, unknown>) {
        return client.post('/Users', data);
    },
    updateUser(data: Record<string, unknown>) {
        return client.put('/Users', data);
    },
    searchCharacters(search: string) {
        return client.get('/Characters', { params: { search } });
    },
    getCharactersForUser(userGuid: string) {
        return client.get('/Characters/ForUser/' + userGuid);
    },
    setCharacterFlags(data: Record<string, unknown>) {
        return client.put('/Characters/Flags', data);
    },
    // Upstream stubs. These routes do not exist on OWSManagement (they belong to
    // OWSInstanceManagement), so the Zones / Zone Instances grids remain non-functional.
    // Kept only so those components still compile.
    addZone(data: Record<string, unknown>) {
        return client.post('/Zones/AddZone', data);
    },
    getZoneInstancesForZone(data: Record<string, unknown>) {
        return client.post('/Instance/GetZoneInstancesForZone', data);
    },
    getStatus() {
        return client.get('/System/Status');
    },

}
