document.addEventListener('DOMContentLoaded', () => {
    const form = document.getElementById('transactionForm');
    const descriptionInput = document.getElementById('description');
    const similarTransactionsContainer = document.getElementById('similarTransactions');
    const recentTransactionsContainer = document.getElementById('recentTransactions');
    const aiInsightsContainer = document.getElementById('aiInsights');
    const loadingOverlay = document.getElementById('loadingOverlay');
    const totalTransactionsElement = document.getElementById('totalTransactions');
    const categorizedCountElement = document.getElementById('categorizedCount');

    // Set default date to today
    document.getElementById('transactionDate').value = new Date().toISOString().split('T')[0];

    // Load recent transactions on page load
    loadRecentTransactions();

    // Handle form submission
    form.addEventListener('submit', async (e) => {
        e.preventDefault();
        
        if (!form.checkValidity()) {
            form.classList.add('was-validated');
            return;
        }

        showLoading(true);

        const transaction = {
            description: descriptionInput.value,
            amount: parseFloat(document.getElementById('amount').value),
            transactionDate: document.getElementById('transactionDate').value
        };

        try {
            const response = await fetch('/api/transactions', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(transaction)
            });

            if (response.ok) {
                const result = await response.json();
                showSuccessMessage('Transaction added successfully!');
                showSimilarTransactions(result.description);
                form.reset();
                document.getElementById('transactionDate').value = new Date().toISOString().split('T')[0];
                loadRecentTransactions();
                updateStats();
            } else {
                showErrorMessage('Error adding transaction');
            }
        } catch (error) {
            console.error('Error:', error);
            showErrorMessage('Error adding transaction');
        } finally {
            showLoading(false);
        }
    });

    // Show similar transactions as user types
    let debounceTimer;
    descriptionInput.addEventListener('input', () => {
        clearTimeout(debounceTimer);
        debounceTimer = setTimeout(() => {
            if (descriptionInput.value.trim()) {
                showSimilarTransactions(descriptionInput.value);
                updateAIInsights(descriptionInput.value);
            } else {
                showEmptySimilarTransactions();
                showDefaultAIInsights();
            }
        }, 500);
    });

    async function showSimilarTransactions(description) {
        try {
            const response = await fetch(`/api/transactions/similar?description=${encodeURIComponent(description)}`);
            if (response.ok) {
                const transactions = await response.json();
                displayTransactions(transactions, similarTransactionsContainer, true);
            }
        } catch (error) {
            console.error('Error:', error);
        }
    }

    async function loadRecentTransactions() {
        try {
            const response = await fetch('/api/transactions');
            if (response.ok) {
                const transactions = await response.json();
                displayTransactions(transactions, recentTransactionsContainer, false);
                updateStats();
            }
        } catch (error) {
            console.error('Error:', error);
        }
    }

    function displayTransactions(transactions, container, showSimilarity) {
        container.innerHTML = '';
        
        if (!transactions || transactions.length === 0) {
            container.innerHTML = `
                <div class="empty-state">
                    <i class="fas fa-${showSimilarity ? 'search' : 'receipt'}"></i>
                    <p>${showSimilarity ? 'No similar transactions found...' : 'No transactions yet...'}</p>
                </div>
            `;
            return;
        }

        transactions.forEach(tx => {
            const div = document.createElement('div');
            div.className = 'transaction-item';

            const labelClass = tx.label ? tx.label.toLowerCase().replace(/\s+/g, '-') : 'uncategorized';
            const labelHtml = `
                <span class="transaction-label ${labelClass}">
                    <i class="fas fa-${getLabelIcon(tx.label)} me-1"></i>
                    ${tx.label || 'Uncategorized'}
                </span>
            `;

            const similarityHtml = showSimilarity ? 
                `<span class="similarity-score">${Math.round((tx.combinedScore || tx.similarity || 0) * 100)}% match</span>` : '';

            // RGS info
            const rgsInfoHtml = (tx.rgsCode || tx.rgsDescription || tx.rgsShortDescription) ? `
                <div class="rgs-info mt-2">
                    ${tx.rgsCode ? `<div><strong>RGS Code:</strong> ${tx.rgsCode}</div>` : ''}
                    ${tx.rgsDescription ? `<div><strong>RGS Description:</strong> ${tx.rgsDescription}</div>` : ''}
                    ${tx.rgsShortDescription ? `<div><strong>RGS Short Description:</strong> ${tx.rgsShortDescription}</div>` : ''}
                </div>
            ` : '';

            div.innerHTML = `
                <div class="transaction-info">
                    <div class="transaction-description">
                        <i class="fas fa-receipt me-2 text-muted"></i>
                        ${tx.description}
                    </div>
                    <div class="transaction-amount">
                        $${tx.amount.toFixed(2)}
                    </div>
                    <div class="transaction-date">
                        <i class="fas fa-calendar me-1"></i>
                        ${new Date(tx.transactionDate).toLocaleDateString()}
                    </div>
                    ${labelHtml}
                    ${similarityHtml}
                    ${rgsInfoHtml}
                </div>
                ${tx.label === "Uncategorized" ? `
                    <div class="mt-2">
                        <div class="input-group">
                            <input type="text" placeholder="Enter label" id="label-input-${tx.id}" class="form-control form-control-sm">
                            <button class="btn btn-sm btn-outline-primary" onclick="updateLabel(${tx.id})">
                                <i class="fas fa-save"></i>
                            </button>
                        </div>
                    </div>
                ` : ''}
            `;
            container.appendChild(div);
        });
    }

    function getLabelIcon(label) {
        const icons = {
            'Transportation': 'car',
            'Groceries': 'shopping-cart',
            'Housing': 'home',
            'Entertainment': 'tv',
            'Utilities': 'bolt',
            'Dining': 'utensils',
            'Health & Fitness': 'heartbeat',
            'Subscriptions': 'sync',
            'Uncategorized': 'question'
        };
        return icons[label] || 'tag';
    }

    function showEmptySimilarTransactions() {
        similarTransactionsContainer.innerHTML = `
            <div class="empty-state">
                <i class="fas fa-search"></i>
                <p>Type a description to see similar transactions...</p>
            </div>
        `;
    }

    function updateAIInsights(description) {
        const insights = [];
        
        if (description.toLowerCase().includes('car') || description.toLowerCase().includes('payment')) {
            insights.push({
                icon: 'car',
                color: 'text-warning',
                text: 'This appears to be a car-related transaction'
            });
        }
        
        if (description.toLowerCase().includes('gas') || description.toLowerCase().includes('fuel')) {
            insights.push({
                icon: 'gas-pump',
                color: 'text-info',
                text: 'Likely a fuel/transportation expense'
            });
        }
        
        if (description.toLowerCase().includes('restaurant') || description.toLowerCase().includes('food')) {
            insights.push({
                icon: 'utensils',
                color: 'text-success',
                text: 'This looks like a dining expense'
            });
        }
        
        if (insights.length === 0) {
            insights.push({
                icon: 'brain',
                color: 'text-primary',
                text: 'AI is analyzing your transaction...'
            });
        }
        
        aiInsightsContainer.innerHTML = insights.map(insight => `
            <div class="insight-item">
                <i class="fas fa-${insight.icon} ${insight.color}"></i>
                <span>${insight.text}</span>
            </div>
        `).join('');
    }

    function showDefaultAIInsights() {
        aiInsightsContainer.innerHTML = `
            <div class="insight-item">
                <i class="fas fa-lightbulb text-warning"></i>
                <span>Type a description to see AI-powered categorization suggestions</span>
            </div>
        `;
    }

    function showLoading(show) {
        if (show) {
            loadingOverlay.classList.remove('d-none');
        } else {
            loadingOverlay.classList.add('d-none');
        }
    }

    function showSuccessMessage(message) {
        showToast(message, 'success');
    }

    function showErrorMessage(message) {
        showToast(message, 'error');
    }

    function showToast(message, type) {
        const toast = document.createElement('div');
        toast.className = `toast-notification ${type}`;
        toast.innerHTML = `
            <i class="fas fa-${type === 'success' ? 'check-circle' : 'exclamation-circle'}"></i>
            <span>${message}</span>
        `;
        
        document.body.appendChild(toast);
        
        setTimeout(() => {
            toast.classList.add('show');
        }, 100);
        
        setTimeout(() => {
            toast.classList.remove('show');
            setTimeout(() => {
                document.body.removeChild(toast);
            }, 300);
        }, 3000);
    }

    async function updateStats() {
        try {
            const response = await fetch('/api/transactions');
            if (response.ok) {
                const transactions = await response.json();
                const total = transactions.length;
                const categorized = transactions.filter(t => t.label && t.label !== 'Uncategorized').length;
                
                totalTransactionsElement.textContent = total;
                categorizedCountElement.textContent = categorized;
            }
        } catch (error) {
            console.error('Error updating stats:', error);
        }
    }

    // Global function for updating labels
    window.updateLabel = async function(transactionId) {
        const input = document.getElementById(`label-input-${transactionId}`);
        const newLabel = input.value.trim();
        
        if (!newLabel) {
            showErrorMessage("Please enter a label.");
            return;
        }
        
        try {
            const response = await fetch(`/api/transactions/${transactionId}/label`, {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(newLabel)
            });
            
            if (response.ok) {
                showSuccessMessage("Label updated successfully!");
                loadRecentTransactions();
                if (descriptionInput.value) {
                    showSimilarTransactions(descriptionInput.value);
                }
            } else {
                showErrorMessage("Error updating label.");
            }
        } catch (error) {
            console.error('Error:', error);
            showErrorMessage("Error updating label.");
        }
    };

    // Global function for refreshing recent transactions
    window.loadRecentTransactions = loadRecentTransactions;
});

// Add toast notification styles
const style = document.createElement('style');
style.textContent = `
    .toast-notification {
        position: fixed;
        top: 20px;
        right: 20px;
        background: white;
        padding: 1rem 1.5rem;
        border-radius: 12px;
        box-shadow: 0 10px 25px rgba(0,0,0,0.1);
        display: flex;
        align-items: center;
        gap: 0.75rem;
        z-index: 10000;
        transform: translateX(100%);
        transition: transform 0.3s ease;
        max-width: 300px;
    }
    
    .toast-notification.show {
        transform: translateX(0);
    }
    
    .toast-notification.success {
        border-left: 4px solid #10b981;
    }
    
    .toast-notification.error {
        border-left: 4px solid #ef4444;
    }
    
    .toast-notification i {
        font-size: 1.25rem;
    }
    
    .toast-notification.success i {
        color: #10b981;
    }
    
    .toast-notification.error i {
        color: #ef4444;
    }
`;
document.head.appendChild(style); 