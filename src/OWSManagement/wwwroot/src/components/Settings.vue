<script setup lang="ts">
    import { reactive, onMounted } from 'vue';
    import { getCustomerGuid, setCustomerGuid, isValidGuid } from '../customerGuid';
    import owsApi from '../owsApi';

    interface Data {
        customerGuid: string,
        savedMessage: string,
        testResult: string,
        testOk: boolean
    }

    const data: Data = reactive({
        customerGuid: '',
        savedMessage: '',
        testResult: '',
        testOk: false
    });

    function save() {
        setCustomerGuid(data.customerGuid);
        data.savedMessage = 'Saved.';
        data.testResult = '';
    }

    function test() {
        setCustomerGuid(data.customerGuid);
        owsApi.getUsers().then((response: any) => {
            const count = Array.isArray(response.data) ? response.data.length : 0;
            data.testOk = true;
            data.testResult = `Connected. ${count} user(s) visible for this CustomerGUID.`;
        }).catch((error: any) => {
            data.testOk = false;
            data.testResult = error?.response?.status === 401
                ? 'Rejected (401). The CustomerGUID is missing or not a valid GUID.'
                : `Request failed: ${error?.message ?? 'unknown error'}`;
        });
    }

    onMounted(() => {
        data.customerGuid = getCustomerGuid();
    });
</script>

<template>
<v-container>
    <v-card class="settings-card">
        <v-card-title>Settings</v-card-title>
        <v-card-text>
            <p class="mb-4">
                Every call this console makes sends the CustomerGUID below as the
                <code>X-CustomerGUID</code> header. Find it in the <code>Customers</code>
                table, or in the launch config the game client uses.
            </p>

            <v-text-field v-model="data.customerGuid"
                          label="CustomerGUID"
                          placeholder="00000000-0000-0000-0000-000000000000"
                          :error="data.customerGuid.length > 0 && !isValidGuid(data.customerGuid)"
                          :error-messages="data.customerGuid.length > 0 && !isValidGuid(data.customerGuid) ? 'Not a valid GUID' : []"
                          spellcheck="false"></v-text-field>

            <v-btn color="success" class="mr-4" @click="save" style="margin-right: 12px;">Save</v-btn>
            <v-btn color="primary" @click="test">Test connection</v-btn>

            <v-alert v-if="data.savedMessage" type="success" density="compact" class="mt-4" style="margin-top: 16px;">
                {{ data.savedMessage }}
            </v-alert>
            <v-alert v-if="data.testResult" :type="data.testOk ? 'success' : 'error'" density="compact" class="mt-4" style="margin-top: 16px;">
                {{ data.testResult }}
            </v-alert>

            <v-alert type="warning" density="compact" style="margin-top: 32px;">
                This console has no login of its own. The CustomerGUID is a tenant
                identifier that the game client already sends on every request, so anyone
                who can reach this page can edit every account in it. Keep the service bound
                to 127.0.0.1 and reach it over an SSH tunnel.
            </v-alert>
        </v-card-text>
    </v-card>
</v-container>
</template>

<style scoped>
    .settings-card {
        max-width: 720px;
    }
</style>
